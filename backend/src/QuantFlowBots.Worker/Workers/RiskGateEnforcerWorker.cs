using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QuantFlowBots.Application.Risk;
using QuantFlowBots.Application.Streaming;
using QuantFlowBots.Application.Trading;
using QuantFlowBots.Domain.Enums;
using QuantFlowBots.Infrastructure.Persistence;

namespace QuantFlowBots.Worker.Workers;

/// <summary>
/// Safety net: every 30s, look for any open position whose symbol is in SymbolRiskGate.
/// Closes it via the trading dispatcher immediately.
///
/// Why this exists when BinanceAnnouncementWorker already auto-closes on block:
///   - The block→close path inside the announcement worker only fires when a symbol transitions
///     un→blocked. If a process restart loses the gate's in-memory state, the announcement worker
///     re-discovers the block on next poll but does NOT re-fire OnBlocked → positions opened in
///     the meantime would never get closed by the announcement path alone.
///   - SymbolStatusReconcilerWorker (every 5min) also blocks but does not auto-close, by design.
///   - A position could be opened manually via API outside the bot pipeline.
///
/// Enforcer eliminates all these gaps with a tight upper bound of 30s.
/// </summary>
public sealed class RiskGateEnforcerWorker(
    IServiceScopeFactory scopeFactory,
    SymbolRiskGate riskGate,
    ILogger<RiskGateEnforcerWorker> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("RiskGateEnforcerWorker started ({Interval}s sweep).", SweepInterval.TotalSeconds);
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SweepAsync(stoppingToken); }
            catch (Exception ex) { logger.LogWarning(ex, "RiskGateEnforcer sweep failed"); }
            try { await Task.Delay(SweepInterval, stoppingToken); } catch { return; }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var blocked = riskGate.Snapshot().Select(f => f.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (blocked.Count == 0) return;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QuantFlowBotsDbContext>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ITradingDispatcher>();
        var botBus = scope.ServiceProvider.GetRequiredService<IBotEventBus>();

        // Pull open positions whose symbol is blocked. Join through Symbol so we don't have to
        // hold all symbols in memory.
        var victims = await db.Positions
            .Where(p => p.Status == PositionStatus.Open && blocked.Contains(p.Symbol!.Code))
            .Select(p => new { p.Id, p.BotId, p.SymbolId, p.Quantity, p.EntryPrice, SymbolCode = p.Symbol!.Code })
            .ToListAsync(ct);

        if (victims.Count == 0) return;
        logger.LogWarning("RiskGateEnforcer found {N} open position(s) on blocked symbols → closing", victims.Count);

        foreach (var p in victims)
        {
            try
            {
                await dispatcher.ExecuteAsync(new PaperOrderRequest(
                    p.BotId, null, p.SymbolId, OrderSide.Sell, p.Quantity, p.EntryPrice, "auto:risk_block"), ct);
                await botBus.PublishAsync(new BotEvent(p.BotId, "auto_close",
                    $"risk_block enforcer closed {p.SymbolCode} qty={p.Quantity}", DateTimeOffset.UtcNow), ct);
                logger.LogWarning("Enforcer closed position {Pos} on {Symbol}", p.Id, p.SymbolCode);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Enforcer close failed for {Pos} on {Symbol}", p.Id, p.SymbolCode);
            }
        }
    }
}

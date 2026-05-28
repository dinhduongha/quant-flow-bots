using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QuantFlowBots.Application.Risk;
using QuantFlowBots.Infrastructure.Exchanges.Binance;
using QuantFlowBots.Infrastructure.Persistence;

namespace QuantFlowBots.Worker.Workers;

/// <summary>
/// Cross-checks our active USDT symbols against Binance /api/v3/exchangeInfo every 30 minutes.
/// When Binance reports status != "TRADING" (HALT / BREAK / AUCTION_MATCH) or the symbol has
/// vanished entirely, we:
///   - Set Symbol.IsActive = false in the DB (durable — survives restart)
///   - Block in SymbolRiskGate (live — stops bot dispatch immediately)
/// </summary>
public sealed class SymbolStatusReconcilerWorker(
    IServiceScopeFactory scopeFactory,
    BinanceRestClient binance,
    SymbolRiskGate riskGate,
    ILogger<SymbolStatusReconcilerWorker> logger) : BackgroundService
{
    // 5min cadence: /api/v3/exchangeInfo is weight=20 — even 1/min would be fine. We pick 5min
    // to ensure status flips reach the bot within 5min of Binance flagging the symbol.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("SymbolStatusReconcilerWorker started.");
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ReconcileAsync(stoppingToken); }
            catch (Exception ex) { logger.LogWarning(ex, "SymbolStatusReconciler failed"); }
            try { await Task.Delay(PollInterval, stoppingToken); } catch { return; }
        }
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        // BinanceRestClient.GetSymbolsAsync filters status==TRADING, so use a raw call here to
        // observe non-trading rows too.
        var http = (HttpClient?)null;
        // Reuse the typed client's underlying HttpClient via reflection-free path: just call the
        // public method again — it already returns only TRADING symbols. We compare presence/absence.
        var live = await binance.GetSymbolsAsync(ct);
        var liveCodes = live.Select(s => s.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QuantFlowBotsDbContext>();

        var ours = await db.Symbols.Where(s => s.IsActive && s.QuoteAsset == "USDT").ToListAsync(ct);
        var newlyDelisted = 0;
        foreach (var s in ours)
        {
            if (liveCodes.Contains(s.Code)) continue;
            // Symbol either delisted or moved to non-TRADING status — both mean don't trade it.
            s.IsActive = false;
            await riskGate.BlockAsync(s.Code, "binance_status_not_trading", "exchange_info", null, ct);
            newlyDelisted++;
            logger.LogWarning("Symbol {Symbol} no longer TRADING on Binance → deactivated + risk-blocked", s.Code);
        }
        if (newlyDelisted > 0) await db.SaveChangesAsync(ct);
    }
}

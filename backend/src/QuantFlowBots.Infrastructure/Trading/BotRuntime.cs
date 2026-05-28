using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QuantFlowBots.Application.Exchanges;
using QuantFlowBots.Application.Risk;
using QuantFlowBots.Application.Strategies;
using QuantFlowBots.Application.Streaming;
using QuantFlowBots.Domain.Entities;
using QuantFlowBots.Domain.Enums;
using QuantFlowBots.Infrastructure.Persistence;
using QuantFlowBots.Infrastructure.Strategies;
using QuantFlowBots.Infrastructure.Trading.BotKinds;

namespace QuantFlowBots.Infrastructure.Trading;

public sealed class BotRuntime(
    IServiceScopeFactory scopeFactory,
    BotKindRouter router,
    StrategyFactory strategyFactory,
    SymbolRiskGate riskGate,
    ILogger<BotRuntime> logger)
{
    private readonly ConcurrentDictionary<Guid, ActiveBot> _active = new();

    public IReadOnlyDictionary<Guid, ActiveBot> Active => _active;

    public async Task LoadRunningAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QuantFlowBotsDbContext>();
        var bots = await db.Bots
            .Where(b => b.State == BotState.Running)
            .Include(b => b.Strategy)
            .Include(b => b.Symbol)
            .ToListAsync(cancellationToken);
        foreach (var bot in bots) Activate(bot);
        logger.LogInformation("BotRuntime loaded {Count} running bots.", bots.Count);
    }

    public void Activate(Bot bot)
    {
        if (bot.Symbol is null) return;
        IStrategy? strategy = null;
        if (bot.Kind == BotKind.Signal && bot.Strategy is not null)
        {
            strategy = strategyFactory.Create(bot.Strategy.Kind);
            strategy.Configure(StrategyBase.ParseJson(bot.Strategy.ParametersJson));
        }
        _active[bot.Id] = new ActiveBot(bot.Id, bot.UserId, bot.Symbol.Code, bot.SymbolId, bot.MaxPositionSize, bot.Kind, strategy);
        logger.LogInformation("BotRuntime activated bot {BotId} symbol={Symbol} kind={Kind}", bot.Id, bot.Symbol.Code, bot.Kind);
    }

    public void Deactivate(Guid botId)
    {
        if (_active.TryRemove(botId, out _))
            logger.LogInformation("BotRuntime deactivated bot {BotId}", botId);
    }

    public async Task OnCandleClosedAsync(KlineEvent evt, CancellationToken cancellationToken)
    {
        if (!evt.Candle.IsClosed) return;
        if (riskGate.IsBlocked(evt.Symbol))
        {
            // Don't dispatch — symbol flagged (delist/hack/suspend). Auto-close path runs separately.
            return;
        }
        var matches = _active.Values.Where(b => string.Equals(b.SymbolCode, evt.Symbol, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) return;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QuantFlowBotsDbContext>();

        foreach (var bot in matches)
        {
            try
            {
                var history = await LoadHistoryAsync(db, bot, evt.Candle, cancellationToken);
                var ctx = new BotKindContext(bot.BotId, bot.UserId, bot.SymbolId, bot.SymbolCode,
                    bot.MaxPositionSize, bot.Strategy, evt.Candle, history);
                await router.DispatchAsync(bot.Kind, ctx, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "BotRuntime.Dispatch failed for bot {BotId}", bot.BotId);
            }
        }
    }

    private static async Task<IReadOnlyList<CandleData>> LoadHistoryAsync(
        QuantFlowBotsDbContext db, ActiveBot bot, CandleData candle, CancellationToken cancellationToken)
    {
        var warmup = bot.Strategy?.WarmupBars ?? 64;
        var rows = await db.Candles
            .Where(c => c.SymbolId == bot.SymbolId && c.Interval == candle.Interval)
            .OrderByDescending(c => c.OpenTime)
            .Take(warmup + 2)
            .ToListAsync(cancellationToken);
        rows.Reverse();
        return rows.Select(c => new CandleData(
            bot.SymbolCode, c.Interval, c.OpenTime, c.CloseTime,
            c.Open, c.High, c.Low, c.Close, c.Volume, c.QuoteVolume, c.TradeCount, true)).ToList();
    }
}

public sealed record ActiveBot(
    Guid BotId,
    Guid UserId,
    string SymbolCode,
    int SymbolId,
    decimal MaxPositionSize,
    BotKind Kind,
    IStrategy? Strategy);

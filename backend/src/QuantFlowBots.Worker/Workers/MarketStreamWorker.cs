using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuantFlowBots.Application.Streaming;
using QuantFlowBots.Domain.Enums;
using QuantFlowBots.Infrastructure.Exchanges.Binance;
using QuantFlowBots.Infrastructure.Persistence;

namespace QuantFlowBots.Worker.Workers;

/// <summary>
/// Drives the market WebSocket subscription. We subscribe kline_1m only for the symbols we
/// actually consume — the configured WatchSymbols plus the symbols of any currently-running bot —
/// NOT every USDT pair. Streaming all ~435 pairs cost ~70MB/s of allocation churn (JSON parsing)
/// for data nobody stored or used (volume-spike, the original reason, was removed). The set is
/// refreshed on a short cycle so turning a bot on/off starts/stops its candle feed within ~1 min,
/// and <see cref="IMarketStreamClient.SetSubscriptions"/> only reconnects when the set truly changes.
/// </summary>
public sealed class MarketStreamWorker(
    IMarketStreamClient stream,
    IOptions<BinanceOptions> options,
    IServiceScopeFactory scopeFactory,
    ILogger<MarketStreamWorker> logger) : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(45);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var watch = options.Value.WatchSymbols;

        var klineSymbols = await ComputeKlineSymbolsAsync(watch, stoppingToken);
        stream.SetSubscriptions(watch, klineSymbols, CandleInterval.OneMinute);
        logger.LogInformation(
            "MarketStreamWorker: {T} tickers + kline_1m for {K} symbols (watched + running bots).",
            watch.Length, klineSymbols.Count);

        var runTask = stream.RunAsync(stoppingToken);
        var refreshTask = RefreshLoopAsync(watch, stoppingToken);
        await Task.WhenAll(runTask, refreshTask);
    }

    private async Task RefreshLoopAsync(string[] watch, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(RefreshInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }

            try
            {
                var klineSymbols = await ComputeKlineSymbolsAsync(watch, stoppingToken);
                stream.SetSubscriptions(watch, klineSymbols, CandleInterval.OneMinute); // no-op if unchanged
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "MarketStreamWorker: subscription refresh failed, keeping current set.");
            }
        }
    }

    /// <summary>Watched symbols ∪ symbols of currently-running bots.</summary>
    private async Task<IReadOnlyCollection<string>> ComputeKlineSymbolsAsync(string[] watch, CancellationToken cancellationToken)
    {
        var set = new HashSet<string>(watch, StringComparer.OrdinalIgnoreCase);
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<QuantFlowBotsDbContext>();
            var botSymbols = await db.Bots
                .Where(b => b.State == BotState.Running && b.Symbol != null)
                .Select(b => b.Symbol!.Code)
                .Distinct()
                .ToListAsync(cancellationToken);
            foreach (var code in botSymbols) set.Add(code);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MarketStreamWorker: failed loading running-bot symbols, using watched only.");
        }
        return set;
    }
}

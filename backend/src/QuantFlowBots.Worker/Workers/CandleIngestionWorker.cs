using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuantFlowBots.Application.Streaming;
using QuantFlowBots.Domain.Entities;
using QuantFlowBots.Infrastructure.Exchanges.Binance;
using QuantFlowBots.Infrastructure.Persistence;

namespace QuantFlowBots.Worker.Workers;

public sealed class CandleIngestionWorker(
    IMarketEventBus bus,
    IOptions<BinanceOptions> options,
    IServiceScopeFactory scopeFactory,
    ILogger<CandleIngestionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var watch = new HashSet<string>(options.Value.WatchSymbols, StringComparer.OrdinalIgnoreCase);
        logger.LogInformation("CandleIngestionWorker persisting candles for {Count} watched symbols only.", watch.Count);

        await foreach (var evt in bus.SubscribeKlines().ReadAllAsync(stoppingToken))
        {
            if (!evt.Candle.IsClosed) continue;
            if (!watch.Contains(evt.Symbol)) continue;
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<QuantFlowBotsDbContext>();
                var symbol = await db.Symbols.FirstOrDefaultAsync(s => s.Code == evt.Symbol, stoppingToken);
                if (symbol is null) continue;

                var exists = await db.Candles.AnyAsync(c =>
                    c.SymbolId == symbol.Id && c.Interval == evt.Candle.Interval && c.OpenTime == evt.Candle.OpenTime,
                    stoppingToken);
                if (exists) continue;

                db.Candles.Add(new Candle
                {
                    SymbolId = symbol.Id,
                    Interval = evt.Candle.Interval,
                    OpenTime = evt.Candle.OpenTime,
                    CloseTime = evt.Candle.CloseTime,
                    Open = evt.Candle.Open,
                    High = evt.Candle.High,
                    Low = evt.Candle.Low,
                    Close = evt.Candle.Close,
                    Volume = evt.Candle.Volume,
                    QuoteVolume = evt.Candle.QuoteVolume,
                    TradeCount = evt.Candle.TradeCount
                });
                await db.SaveChangesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "CandleIngestion failed for {Symbol}", evt.Symbol);
            }
        }
    }
}

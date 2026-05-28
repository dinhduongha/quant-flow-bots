using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuantFlowBots.Application.Streaming;
using QuantFlowBots.Infrastructure.Exchanges.Binance;
using QuantFlowBots.Infrastructure.Persistence;
using QuantFlowBots.Infrastructure.Trading;

namespace QuantFlowBots.Worker.Workers;

/// <summary>
/// True-realtime wall detection via Binance combined WebSocket. Subscribes to
/// <c>&lt;symbol&gt;@depth20@1000ms</c> for the top-N USDT pairs by 24h volume.
/// Each depth update (~1/sec per symbol) is scanned for large single-level orders and
/// published on <see cref="IOrderBookWallBus"/>, feeding the same cache + Telegram pipeline
/// as the REST scanner.
///
/// Trade-offs vs REST scanner:
///   + Latency: ~100-500ms vs 30-60s
///   + No Binance weight cost (WebSocket connection only)
///   - Single websocket: if it drops, we miss data until reconnect (handled with backoff)
///   - Symbol list refreshed every <see cref="ResubInterval"/> — high-volatility coins that
///     suddenly enter top-N have to wait for the next refresh to be watched.
/// </summary>
public sealed class OrderBookWallStreamWorker(
    IServiceScopeFactory scopeFactory,
    TickerSnapshotCache tickerCache,
    OrderBookWallCache wallCache,
    IOrderBookWallBus bus,
    IOptions<OrderBookWallOptions> options,
    IOptions<BinanceOptions> binanceOptions,
    ILogger<OrderBookWallStreamWorker> logger) : BackgroundService
{
    // Refresh symbol list every 30 min — volume rankings don't shift faster than that for
    // anything actionable; trying to track minute-by-minute would thrash the WS connection.
    private static readonly TimeSpan ResubInterval = TimeSpan.FromMinutes(30);

    // 1000ms is the steady-state Partial Book Depth tier. 100ms exists but bursts to ~1000 msg/s
    // for 100 symbols — way more than we need for wall detection.
    private const string DepthRate = "1000ms";
    private const int DepthLevels = 20;   // top 20 bids + asks per snapshot

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opt = options.Value;
        // MaxSymbols <= 0 means "scan every eligible USDT pair". Upper-bounded at 500 to stay within
        // a single combined-stream WS connection (Binance allows up to 1024 streams/connection).
        var maxSymbols = opt.MaxSymbols <= 0 ? 500 : Math.Clamp(opt.MaxSymbols, 1, 500);
        logger.LogInformation("OrderBookWallStreamWorker started — top {N} symbols, depth{L}@{Rate}", maxSymbols, DepthLevels, DepthRate);

        // Wait for the ticker cache to be hydrated (MarketStreamWorker / SignalScanner already
        // pull this on boot). 20s upper bound — if it's still empty after that, scan anyway with
        // whatever's there; we'll re-attempt at the next ResubInterval.
        await WaitForTickersAsync(TimeSpan.FromSeconds(20), stoppingToken);

        var backoff = TimeSpan.FromSeconds(1);
        var resubAt = DateTimeOffset.UtcNow + ResubInterval;
        string[] currentSymbols = [];

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                currentSymbols = await PickTopSymbolsAsync(maxSymbols, opt, stoppingToken);
                if (currentSymbols.Length == 0)
                {
                    logger.LogWarning("Wall stream: no eligible symbols (ticker cache empty?). Retrying in 30s.");
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                    continue;
                }

                var streams = currentSymbols.Select(s => $"{s.ToLowerInvariant()}@depth{DepthLevels}@{DepthRate}").ToArray();
                using var ws = new ClientWebSocket();
                var url = $"{binanceOptions.Value.WebSocketBaseUrl}/stream?streams={string.Join("/", streams)}";
                logger.LogInformation("Wall stream connecting — {Count} streams", streams.Length);
                await ws.ConnectAsync(new Uri(url), stoppingToken);
                logger.LogInformation("Wall stream connected.");
                backoff = TimeSpan.FromSeconds(1);
                resubAt = DateTimeOffset.UtcNow + ResubInterval;

                await PumpAsync(ws, opt, () => DateTimeOffset.UtcNow >= resubAt, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Wall stream error — reconnecting in {Backoff}s", backoff.TotalSeconds);
                try { await Task.Delay(backoff, stoppingToken); } catch { return; }
                backoff = TimeSpan.FromSeconds(Math.Min(30, backoff.TotalSeconds * 2));
            }
        }
    }

    /// <summary>Pull the largest-volume eligible USDT pairs from the ticker cache.</summary>
    private async Task<string[]> PickTopSymbolsAsync(int n, OrderBookWallOptions opt, CancellationToken ct)
    {
        var tickers = await tickerCache.GetAsync(ct);
        if (tickers.Count == 0) return [];

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QuantFlowBotsDbContext>();
        var quoteFilter = opt.QuoteAssets is { Length: > 0 } ? opt.QuoteAssets : ["USDT"];
        var excludedBases = opt.ExcludedBaseAssets.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var eligible = await db.Symbols
            .Where(s => s.IsActive)
            .Where(s => quoteFilter.Contains(s.QuoteAsset))
            .Where(s => !excludedBases.Contains(s.BaseAsset))
            .Select(s => s.Code)
            .ToListAsync(ct);
        var eligibleSet = eligible.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return tickers
            .Where(t => eligibleSet.Contains(t.Symbol))
            .Where(t => t.Symbol.All(c => c < 128 && (char.IsLetterOrDigit(c) || c == '_')))  // skip garbage tickers
            .OrderByDescending(t => t.QuoteVolume)
            .Take(n)
            .Select(t => t.Symbol)
            .ToArray();
    }

    /// <summary>Read frames from WS, parse partial depth, run wall detection, publish.</summary>
    private async Task PumpAsync(ClientWebSocket ws, OrderBookWallOptions opt, Func<bool> shouldResub, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            if (shouldResub())
            {
                logger.LogInformation("Wall stream: scheduled resubscribe — refreshing symbol list");
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "resub", ct);
                return;
            }

            var sb = new StringBuilder();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) return;
                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            } while (!result.EndOfMessage);

            try { ProcessMessage(sb.ToString(), opt, ct); }
            catch (Exception ex) { logger.LogDebug(ex, "Wall stream parse failed"); }
        }
    }

    private void ProcessMessage(string payload, OrderBookWallOptions opt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(payload)) return;
        using var doc = JsonDocument.Parse(payload);
        if (!doc.RootElement.TryGetProperty("stream", out var streamEl)) return;
        var stream = streamEl.GetString() ?? string.Empty;
        if (!stream.Contains("@depth", StringComparison.OrdinalIgnoreCase)) return;

        // Stream name format: "btcusdt@depth20@1000ms" — symbol is the part before '@'.
        var atIdx = stream.IndexOf('@');
        if (atIdx <= 0) return;
        var symbol = stream[..atIdx].ToUpperInvariant();

        var data = doc.RootElement.GetProperty("data");
        var bids = ParseLevels(data, "bids");
        var asks = ParseLevels(data, "asks");
        if (bids.Length == 0 || asks.Length == 0) return;

        var bestBid = bids[0].Price;
        var bestAsk = asks[0].Price;
        var mid = (bestBid + bestAsk) / 2m;
        if (mid <= 0m) return;

        // Dynamic stable-pair skip — same logic as REST scanner so behaviour matches.
        if (opt.StablePairPriceBandPercent > 0m &&
            Math.Abs(mid - 1m) / 1m * 100m <= opt.StablePairPriceBandPercent) return;

        var avgBid = AvgNotional(bids);
        var avgAsk = AvgNotional(asks);

        ScanSide(symbol, "Bid", bids, mid, avgBid, opt, ct);
        ScanSide(symbol, "Ask", asks, mid, avgAsk, opt, ct);
    }

    private void ScanSide(string symbol, string side, (decimal Price, decimal Qty)[] levels,
        decimal mid, decimal avgNotional, OrderBookWallOptions opt, CancellationToken ct)
    {
        foreach (var lvl in levels)
        {
            var notional = lvl.Price * lvl.Qty;
            if (notional < opt.DetectionFloorUsdt) continue;
            var distPct = Math.Abs(lvl.Price - mid) / mid * 100m;
            if (distPct > opt.MaxDistanceFromMidPercent) continue;
            var multiplier = avgNotional > 0m ? notional / avgNotional : 0m;
            var evt = new OrderBookWallEvent(
                symbol, side, lvl.Price, lvl.Qty, notional, mid, distPct, multiplier, DateTimeOffset.UtcNow);
            wallCache.Upsert(evt);
            _ = bus.PublishAsync(evt, ct);   // fire-and-forget — bus channel is bounded with drop-oldest
        }
    }

    private static (decimal Price, decimal Qty)[] ParseLevels(JsonElement data, string side)
    {
        if (!data.TryGetProperty(side, out var arr) || arr.ValueKind != JsonValueKind.Array) return [];
        var result = new (decimal, decimal)[arr.GetArrayLength()];
        var i = 0;
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() < 2) continue;
            var price = ParseDec(item[0].GetString());
            var qty = ParseDec(item[1].GetString());
            result[i++] = (price, qty);
        }
        return result[..i];
    }

    private static decimal ParseDec(string? s) =>
        decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0m;

    private static decimal AvgNotional((decimal Price, decimal Qty)[] levels)
    {
        if (levels.Length == 0) return 0m;
        decimal sum = 0m;
        var n = Math.Min(levels.Length, 20);
        for (var i = 0; i < n; i++) sum += levels[i].Price * levels[i].Qty;
        return sum / n;
    }

    private async Task WaitForTickersAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var t = await tickerCache.GetAsync(ct);
            if (t.Count > 0) return;
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct); } catch { return; }
        }
    }
}

using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuantFlowBots.Application.Exchanges;
using QuantFlowBots.Application.Streaming;
using QuantFlowBots.Domain.Enums;

namespace QuantFlowBots.Infrastructure.Exchanges.Binance;

public sealed class BinanceMarketStreamClient(
    IOptions<BinanceOptions> options,
    IMarketEventBus bus,
    ILogger<BinanceMarketStreamClient> logger) : IMarketStreamClient
{
    private readonly BinanceOptions _opt = options.Value;
    private readonly object _lock = new();
    private string[] _streams = [];
    private CancellationTokenSource? _resubCts;

    public string ExchangeCode => "binance";

    public void SetSubscriptions(IEnumerable<string> tickerSymbols, IEnumerable<string> klineSymbols, CandleInterval klineInterval)
    {
        var bIv = ToBinanceInterval(klineInterval);
        // SortedSet → deterministic ordering so SequenceEqual reliably detects "no change".
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in tickerSymbols) set.Add(s.ToLowerInvariant() + "@ticker");
        foreach (var s in klineSymbols) set.Add($"{s.ToLowerInvariant()}@kline_{bIv}");
        var next = set.ToArray();

        lock (_lock)
        {
            if (_streams.SequenceEqual(next, StringComparer.OrdinalIgnoreCase)) return; // unchanged — don't churn the WS
            _streams = next;
            _resubCts?.Cancel(); // wake RunAsync's pump so it reconnects with the new stream list
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!cancellationToken.IsCancellationRequested)
        {
            string[] streams;
            CancellationTokenSource resubCts;
            lock (_lock)
            {
                streams = _streams;
                resubCts = _resubCts = new CancellationTokenSource();
            }

            if (streams.Length == 0)
            {
                // Nothing to watch yet (no watched symbols + no running bots). Wait for a SetSubscriptions
                // that adds streams — it cancels resubCts, so this delay ends early.
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, resubCts.Token);
                try { await Task.Delay(TimeSpan.FromSeconds(5), idle.Token); } catch { }
                continue;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, resubCts.Token);
            try
            {
                using var ws = new ClientWebSocket();
                var url = $"{_opt.WebSocketBaseUrl}/stream?streams={string.Join("/", streams)}";
                logger.LogInformation("BinanceMarketStream connecting ({Count} streams).", streams.Length);
                await ws.ConnectAsync(new Uri(url), linked.Token);
                logger.LogInformation("BinanceMarketStream connected ({Count} streams).", streams.Length);
                backoff = TimeSpan.FromSeconds(1);

                var buffer = new byte[64 * 1024];
                while (ws.State == WebSocketState.Open && !linked.IsCancellationRequested)
                {
                    var sb = new StringBuilder();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(buffer, linked.Token);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                        sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    } while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close) break;
                    await HandleAsync(sb.ToString(), cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (OperationCanceledException)
            {
                // Resubscribe was requested — reconnect immediately with the new stream list, no backoff.
                logger.LogInformation("BinanceMarketStream resubscribing — symbol set changed.");
                continue;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "BinanceMarketStream error, reconnecting in {Backoff}s.", backoff.TotalSeconds);
                try { await Task.Delay(backoff, cancellationToken); } catch (OperationCanceledException) { break; }
                backoff = TimeSpan.FromSeconds(Math.Min(30, backoff.TotalSeconds * 2));
            }
        }
    }

    private async Task HandleAsync(string payload, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payload)) return;
        using var doc = JsonDocument.Parse(payload);
        if (!doc.RootElement.TryGetProperty("stream", out var streamEl)) return;
        var stream = streamEl.GetString() ?? string.Empty;
        var data = doc.RootElement.GetProperty("data");

        if (stream.EndsWith("@ticker", StringComparison.OrdinalIgnoreCase))
        {
            var symbol = data.GetProperty("s").GetString()!;
            var price = Parse(data.GetProperty("c").GetString());
            var pct = Parse(data.GetProperty("P").GetString());
            var qVol = Parse(data.GetProperty("q").GetString());
            await bus.PublishAsync(new TickerEvent("binance", symbol, price, pct, qVol, DateTimeOffset.UtcNow), cancellationToken);
            return;
        }

        if (stream.Contains("@kline_", StringComparison.OrdinalIgnoreCase))
        {
            var k = data.GetProperty("k");
            var symbol = data.GetProperty("s").GetString()!;
            var intervalStr = k.GetProperty("i").GetString()!;
            var candle = new CandleData(
                symbol,
                FromBinanceInterval(intervalStr),
                DateTimeOffset.FromUnixTimeMilliseconds(k.GetProperty("t").GetInt64()),
                DateTimeOffset.FromUnixTimeMilliseconds(k.GetProperty("T").GetInt64()),
                Parse(k.GetProperty("o").GetString()),
                Parse(k.GetProperty("h").GetString()),
                Parse(k.GetProperty("l").GetString()),
                Parse(k.GetProperty("c").GetString()),
                Parse(k.GetProperty("v").GetString()),
                Parse(k.GetProperty("q").GetString()),
                k.GetProperty("n").GetInt32(),
                k.GetProperty("x").GetBoolean(),
                Parse(k.GetProperty("Q").GetString()));
            await bus.PublishAsync(new KlineEvent("binance", symbol, candle, DateTimeOffset.UtcNow), cancellationToken);
        }
    }

    private static decimal Parse(string? value) =>
        decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0m;

    private static string ToBinanceInterval(CandleInterval i) => i switch
    {
        CandleInterval.OneMinute => "1m",
        CandleInterval.FiveMinutes => "5m",
        CandleInterval.FifteenMinutes => "15m",
        CandleInterval.ThirtyMinutes => "30m",
        CandleInterval.OneHour => "1h",
        CandleInterval.TwoHours => "2h",
        CandleInterval.FourHours => "4h",
        CandleInterval.OneDay => "1d",
        _ => "1m"
    };

    private static CandleInterval FromBinanceInterval(string s) => s switch
    {
        "1m" => CandleInterval.OneMinute,
        "5m" => CandleInterval.FiveMinutes,
        "15m" => CandleInterval.FifteenMinutes,
        "30m" => CandleInterval.ThirtyMinutes,
        "1h" => CandleInterval.OneHour,
        "2h" => CandleInterval.TwoHours,
        "4h" => CandleInterval.FourHours,
        "1d" => CandleInterval.OneDay,
        _ => CandleInterval.OneMinute
    };
}

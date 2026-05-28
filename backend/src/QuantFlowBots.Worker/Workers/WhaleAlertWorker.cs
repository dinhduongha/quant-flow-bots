using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QuantFlowBots.Application.Exchanges;
using QuantFlowBots.Domain.Entities;
using QuantFlowBots.Domain.Enums;
using QuantFlowBots.Infrastructure.Exchanges.Binance;
using QuantFlowBots.Infrastructure.Persistence;
using StackExchange.Redis;

namespace QuantFlowBots.Worker.Workers;

/// <summary>
/// Scans the top-100 USDT pairs every minute. For each user with WhaleAlertEnabled, for
/// each configured interval (5m/15m/1h/...), fetches the latest 2 klines and emits a
/// Telegram alert when the closed (or in-progress, depending on Mode) candle's quoteVolume
/// is at least Multiplier × the previous closed candle's. Direction (Buy / Sell) is inferred
/// from price action (close ?? open).
///
/// Dedupe: Redis key `whale:{userId}:{symbol}:{interval}:{mode}` with TTL = CooldownMinutes.
/// Respects the global Binance gate via the typed HTTP client.
/// </summary>
public sealed class WhaleAlertWorker(
    IServiceScopeFactory scopeFactory,
    TickerSnapshotCache tickerCache,
    IBinanceGate gate,
    IConnectionMultiplexer redis,
    IHttpClientFactory httpClientFactory,
    ILogger<WhaleAlertWorker> logger) : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(60);
    // Backstop against a noisy config flooding Telegram: never send more than this many whale
    // alerts in a single scan pass. Excess is logged so the user knows to raise the multiplier.
    private const int MaxAlertsPerTick = 25;
    private static readonly Random _rng = new();
    private static readonly string[] AllowedIntervals = ["1m", "3m", "5m", "15m", "30m", "1h", "2h", "4h"];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("WhaleAlertWorker started ({Interval}s).", ScanInterval.TotalSeconds);
        // Initial random delay so we don't all fire at the same instant after a restart.
        try { await Task.Delay(TimeSpan.FromSeconds(_rng.Next(5, 25)), stoppingToken); } catch { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var state = await gate.GetStateAsync(stoppingToken);
            if (state.IsOpen)
            {
                var wait = state.Until!.Value - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(_rng.Next(3, 10));
                if (wait < TimeSpan.FromSeconds(15)) wait = TimeSpan.FromSeconds(15);
                logger.LogWarning("WhaleAlert: gate OPEN, sleeping {Wait}s", (int)wait.TotalSeconds);
                try { await Task.Delay(wait, stoppingToken); } catch { return; }
                continue;
            }

            try { await TickAsync(stoppingToken); }
            catch (BinanceGateOpenException) { /* tripped mid-tick — loop yields */ }
            catch (Exception ex) { logger.LogError(ex, "WhaleAlert tick failed"); }

            var jitter = TimeSpan.FromMilliseconds(_rng.Next(-3000, 3000));
            try { await Task.Delay(ScanInterval + jitter, stoppingToken); } catch { return; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QuantFlowBotsDbContext>();
        var binance = scope.ServiceProvider.GetRequiredService<BinanceRestClient>();

        // Read all users who opted in. We collect the union of (interval, userSet) tuples so
        // we only fetch each (symbol, interval) once even when multiple users subscribe.
        var users = await db.Set<UserSettings>()
            .AsNoTracking()
            .Where(s => s.WhaleAlertEnabled
                     && s.WhaleAlertBotToken != null
                     && s.WhaleAlertChatId != null
                     && s.WhaleAlertIntervals != null)
            .ToListAsync(ct);
        if (users.Count == 0) return;

        var intervals = users
            .SelectMany(u => (u.WhaleAlertIntervals ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(s => s.ToLowerInvariant())
            .Where(s => AllowedIntervals.Contains(s))
            .Distinct()
            .ToList();
        if (intervals.Count == 0) return;

        // Scan ALL USDT pairs whose 24h volume clears the LOWEST per-user threshold — that floor IS
        // the scan universe now (was a hard top-100, which silently excluded every lowcap/midcap a
        // user actually wanted). RateLimitManager paces the resulting klines calls so we never burn
        // through the weight budget. The 500 cap is just a pathological-case guard (≈ all USDT pairs).
        var minVolFloor = users.Min(u => u.WhaleAlertMinVolume24h);
        var tickers = await tickerCache.GetAsync(ct);
        var top = tickers
            .Where(t => t.Symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase))
            .Where(t => Math.Abs(t.LastPrice - 1m) > 0.03m)              // skip dollar-pegged stablecoins
            .Where(t => t.QuoteVolume >= minVolFloor)                    // user's Min-vol = the real scan floor
            .OrderByDescending(t => t.QuoteVolume)
            .Take(500)
            .ToList();
        var vol24hBySymbol = top.ToDictionary(t => t.Symbol, t => t.QuoteVolume, StringComparer.OrdinalIgnoreCase);

        // One global fetch size — biggest lookback any user asked for, capped at 50.
        var maxLookback = Math.Min(50, Math.Max(5, users.Max(u => u.WhaleAlertLookback)));

        var detected = 0;
        var suppressed = 0;
        // Track the strongest spike seen this pass so we can log "how close did we get" even when
        // nothing fired — turns "why no alerts?" into an observable number.
        decimal topRatio = 0m; string topSym = "-"; string topIv = "-";
        foreach (var interval in intervals)
        {
            if (ct.IsCancellationRequested) break;
            if (!Enum.TryParse<CandleInterval>(BinanceToEnum(interval), true, out var iv)) continue;

            foreach (var ticker in top)
            {
                if (ct.IsCancellationRequested) break;
                // limit = lookback baseline + 1 current candle.
                var candles = await binance.GetCandlesAsync(ticker.Symbol, iv, null, null, maxLookback + 1, ct);
                if (candles.Count < 3) continue;
                var curr = candles[^1];

                foreach (var user in users)
                {
                    if (!UserWatchesInterval(user, interval)) continue;
                    var useIntrabar = string.Equals(user.WhaleAlertMode, "intrabar", StringComparison.OrdinalIgnoreCase);
                    if (!useIntrabar && !curr.IsClosed) continue;

                    var lb = Math.Min(maxLookback, Math.Max(5, user.WhaleAlertLookback));
                    // Average the last `lb` CLOSED candles ending just before curr.
                    var start = candles.Count - 1 - lb;
                    if (start < 0) start = 0;
                    decimal sum = 0m; var count = 0;
                    for (var i = start; i < candles.Count - 1; i++) { sum += candles[i].QuoteVolume; count++; }
                    if (count == 0 || sum <= 0m) continue;
                    var avg = sum / count;

                    // Volume we judge. In intrabar mode the latest bar is still OPEN, so its raw volume
                    // is only a slice of a full bar. We project it to a full-bar equivalent by elapsed
                    // fraction so a burst is caught before the bar closes — BUT only once the bar is at
                    // least half-elapsed. Projecting earlier (e.g. 12%) divides by a tiny fraction and
                    // turns ordinary front-loaded volume into fake 3-8× spikes (that flooded 240 alerts
                    // in one pass). At ≥50% the projection is at most 2×, so only real bursts survive.
                    var effectiveVol = curr.QuoteVolume;
                    if (useIntrabar && !curr.IsClosed)
                    {
                        var durSec = (curr.CloseTime - curr.OpenTime).TotalSeconds;
                        var elapsedSec = (DateTimeOffset.UtcNow - curr.OpenTime).TotalSeconds;
                        if (durSec <= 0 || elapsedSec < durSec * 0.5) continue; // wait until bar is half-done
                        var frac = Math.Min(1.0, elapsedSec / durSec);
                        effectiveVol = curr.QuoteVolume / (decimal)frac;
                    }
                    var ratio = avg > 0m ? effectiveVol / avg : 0m;
                    if (ratio > topRatio) { topRatio = ratio; topSym = ticker.Symbol; topIv = interval; }
                    if (ratio < user.WhaleAlertMultiplier) continue;

                    if (!vol24hBySymbol.TryGetValue(ticker.Symbol, out var vol24h) || vol24h < user.WhaleAlertMinVolume24h) continue;

                    var direction = curr.Close >= curr.Open ? "buy" : "sell";
                    // Treat null OR empty as "both" — the FE/DB stores "" for the default, and `??`
                    // only catches null, so an empty string used to fail BOTH the "both" check and the
                    // direction match → every alert was silently skipped.
                    var userDir = string.IsNullOrWhiteSpace(user.WhaleAlertDirection) ? "both" : user.WhaleAlertDirection.Trim().ToLowerInvariant();
                    if (userDir != "both" && userDir != direction) continue;

                    // Flood backstop — count over-limit qualifiers but don't send/dedupe them, so a
                    // later pass can still deliver them once the burst thins out.
                    if (detected >= MaxAlertsPerTick) { suppressed++; continue; }

                    var dedupeKey = $"whale:{user.UserId}:{ticker.Symbol}:{interval}:{user.WhaleAlertMode}";
                    var redisDb = redis.GetDatabase();
                    var setOk = await redisDb.StringSetAsync(
                        dedupeKey, "1",
                        TimeSpan.FromMinutes(Math.Max(5, user.WhaleAlertCooldownMinutes)),
                        when: When.NotExists);
                    if (!setOk) continue;

                    await SendTelegramAsync(user, ticker.Symbol, interval, curr, avg, count, ratio, vol24h, direction, candles, ct);
                    detected++;
                }
            }
        }

        logger.LogInformation(
            "WhaleAlert tick: scanned {Symbols} symbols × {Intervals} intervals (vol≥{Floor:0}) · top spike {Top:0.0}× ({Sym} {Iv}) · {Detected} sent{Suppressed}",
            top.Count, intervals.Count, minVolFloor, topRatio, topSym, topIv, detected,
            suppressed > 0 ? $" · {suppressed} suppressed (raise multiplier ×)" : "");
    }

    private static bool UserWatchesInterval(UserSettings user, string interval) =>
        (user.WhaleAlertIntervals ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(s => string.Equals(s, interval, StringComparison.OrdinalIgnoreCase));

    private async Task SendTelegramAsync(
        UserSettings user, string symbol, string interval,
        CandleData curr, decimal avgBaseline, int lookback, decimal ratio, decimal vol24h,
        string direction, IReadOnlyList<CandleData> candles, CancellationToken ct)
    {
        var baseAsset = symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase) ? symbol[..^4] : symbol;
        var pricePct = curr.Open > 0 ? (curr.Close - curr.Open) / curr.Open * 100m : 0m;
        var emoji = direction == "buy" ? "🟢" : "🔴";
        var verb = direction == "buy" ? "bought" : "sold";
        var baseQty = curr.Volume;

        var text =
            $"🐋 *{symbol}* `[Binance]`\n" +
            $"{emoji} Big Whales *{(direction == "buy" ? "Buy" : "Sell")}* Activity\n" +
            $"{FmtBig(baseQty)} {baseAsset} have been {verb}\n\n" +
            $"💰 Price: {FmtPrice(curr.Close)} USDT (*{(pricePct >= 0 ? "+" : "")}{pricePct:F2}%*)\n" +
            $"🚨 Order Size: {FmtUsdt(curr.QuoteVolume)} USDT (*{ratio:F2}×* avg of last {lookback} × {interval})\n" +
            $"📈 Avg baseline: {FmtUsdt(avgBaseline)} USDT\n" +
            $"⌛ Window: *{interval}*\n" +
            $"📊 24h Vol: {FmtUsdt(vol24h)} USDT";

        var chart = WhaleSnapshotRenderer.TryRender(symbol, interval, candles, avgBaseline, ratio, direction);

        try
        {
            using var client = httpClientFactory.CreateClient("telegram");
            client.Timeout = TimeSpan.FromSeconds(12);
            using var resp = chart is not null
                ? await SendPhotoAsync(client, user.WhaleAlertBotToken!, user.WhaleAlertChatId!, chart, text, ct)
                : await client.PostAsync(
                    $"https://api.telegram.org/bot{user.WhaleAlertBotToken}/sendMessage",
                    JsonContent.Create(new { chat_id = user.WhaleAlertChatId, text, parse_mode = "Markdown" }), ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                logger.LogWarning("Telegram whale-alert send failed user={UserId} status={Status} body={Body}",
                    user.UserId, (int)resp.StatusCode, body);
            }
        }
        catch (Exception ex) { logger.LogWarning(ex, "Telegram whale-alert send threw"); }
    }

    private static async Task<HttpResponseMessage> SendPhotoAsync(
        HttpClient client, string token, string chatId, byte[] png, string caption, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(chatId), "chat_id");
        form.Add(new StringContent(caption), "caption");
        form.Add(new StringContent("Markdown"), "parse_mode");
        var photo = new ByteArrayContent(png);
        photo.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(photo, "photo", "whale.png");
        return await client.PostAsync($"https://api.telegram.org/bot{token}/sendPhoto", form, ct);
    }

    private static string BinanceToEnum(string interval) => interval switch
    {
        "1m" => "OneMinute", "3m" => "ThreeMinutes", "5m" => "FiveMinutes",
        "15m" => "FifteenMinutes", "30m" => "ThirtyMinutes",
        "1h" => "OneHour", "2h" => "TwoHours", "4h" => "FourHours",
        _ => interval,
    };

    private static string FmtBig(decimal n)
    {
        if (n >= 1_000_000m) return $"{n / 1_000_000m:F2}M";
        if (n >= 1_000m) return $"{n / 1_000m:F2}K";
        return n.ToString("F2");
    }

    private static string FmtUsdt(decimal n)
    {
        if (n >= 1_000_000_000m) return $"{n / 1_000_000_000m:F2}B";
        if (n >= 1_000_000m) return $"{n / 1_000_000m:F2}M";
        if (n >= 1_000m) return $"{n / 1_000m:F2}K";
        return n.ToString("F0");
    }

    private static string FmtPrice(decimal p)
    {
        if (p >= 1000) return p.ToString("N2");
        if (p >= 1) return p.ToString("F4");
        return p.ToString("G6");
    }
}

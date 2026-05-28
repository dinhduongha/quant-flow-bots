using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QuantFlowBots.Application.Streaming;
using QuantFlowBots.Infrastructure.Exchanges.Binance;
using QuantFlowBots.Infrastructure.Persistence;
using StackExchange.Redis;

namespace QuantFlowBots.Worker.Workers;

/// <summary>
/// Bridges the order-book wall detection pipeline to per-user Telegram alerts. Subscribes to
/// <see cref="IOrderBookWallBus.OnWall"/>, filters each event against every user's WallAlert*
/// settings, and dispatches a Telegram message with Redis dedupe to respect cooldown.
///
/// IMPORTANT — this event is a firehose: the detector raises OnWall for every qualifying price
/// level on every depth refresh (thousands/sec across the watched symbols). So the handler must
/// do as little as possible synchronously and MUST NOT touch the DB per event. We therefore:
///   1. Cache the (small) eligible-user set in-memory and refresh it on a timer — NOT per event.
///      (The original per-event DbContext spun up ~1500 scopes/sec, leaking tens of thousands of
///      EF query graphs and exhausting the Postgres connection pool — a multi-GB heap balloon.)
///   2. Throttle in-memory per symbol|side BEFORE spawning any Task, so the firehose collapses to
///      at most one dispatch per symbol|side per <see cref="LocalThrottle"/>.
/// </summary>
public sealed class WallAlertWorker(
    IServiceScopeFactory scopeFactory,
    IOrderBookWallBus wallBus,
    IConnectionMultiplexer redis,
    IHttpClientFactory httpClientFactory,
    ILogger<WallAlertWorker> logger) : BackgroundService
{
    private static readonly TimeSpan UserRefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LocalThrottle = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<string, long> _lastDispatchTicks = new();
    private volatile CachedUser[] _users = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("WallAlertWorker started — listening on IOrderBookWallBus.OnWall.");
        await RefreshUsersAsync(stoppingToken);
        wallBus.OnWall += HandleWall;
        stoppingToken.Register(() => wallBus.OnWall -= HandleWall);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(UserRefreshInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
            await RefreshUsersAsync(stoppingToken);
        }
    }

    /// <summary>Refresh the cached eligible-user set. Runs on a timer — never per wall event.</summary>
    private async Task RefreshUsersAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<QuantFlowBotsDbContext>();
            var users = await db.UserSettings.AsNoTracking()
                .Where(s => s.WallAlertEnabled && s.WallAlertBotToken != null && s.WallAlertChatId != null)
                .Select(s => new CachedUser(
                    s.UserId, s.WallAlertBotToken!, s.WallAlertChatId!,
                    s.WallAlertMinNotional, s.WallAlertMaxDistancePct, s.WallAlertSide,
                    s.WallAlertCooldownMinutes))
                .ToListAsync(ct);
            _users = users.ToArray();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "WallAlertWorker: refreshing user set failed, keeping previous cache.");
        }
    }

    private void HandleWall(OrderBookWallEvent evt)
    {
        var users = _users;
        if (users.Length == 0) return; // nobody subscribed — drop instantly, zero work

        // Cheap in-memory throttle per symbol|side BEFORE any Task/DB/Redis work. This is what
        // tames the per-level firehose; Redis still enforces the real per-user cooldown below.
        var key = evt.Symbol + "|" + evt.Side;
        var now = DateTimeOffset.UtcNow.UtcTicks;
        var last = _lastDispatchTicks.GetOrAdd(key, 0L);
        if (now - last < LocalThrottle.Ticks) return;
        if (!_lastDispatchTicks.TryUpdate(key, now, last)) return; // lost the race — peer will dispatch

        _ = Task.Run(async () =>
        {
            try { await DispatchAsync(evt, users, CancellationToken.None); }
            catch (Exception ex) { logger.LogWarning(ex, "WallAlertWorker dispatch failed for {Sym}", evt.Symbol); }
        });
    }

    private async Task DispatchAsync(OrderBookWallEvent evt, CachedUser[] users, CancellationToken ct)
    {
        var redisDb = redis.GetDatabase();

        // Order-book snapshot is rendered lazily: only once, and only if at least one user actually
        // clears the cooldown below (so we never fetch depth / draw when everyone's on cooldown).
        byte[]? photo = null;
        var photoReady = false;

        foreach (var u in users)
        {
            if (evt.QuoteNotional < u.MinNotional) continue;
            if (evt.DistanceFromMidPercent > u.MaxDistancePct) continue;
            if (!string.IsNullOrEmpty(u.Side) && !string.Equals(u.Side, evt.Side, StringComparison.OrdinalIgnoreCase)) continue;

            // Dedupe per user × symbol × side — Redis NX with cooldown TTL. Prevents the same
            // wall (often re-detected on every depth refresh) from spamming the chat.
            var key = $"wallalert:{u.UserId}:{evt.Symbol}:{evt.Side}";
            var ttl = TimeSpan.FromMinutes(Math.Max(1, u.CooldownMinutes));
            var firstSeen = await redisDb.StringSetAsync(key, evt.At.ToUnixTimeSeconds(), ttl, when: When.NotExists);
            if (!firstSeen) continue;

            // First recipient that clears the cooldown triggers the one-time depth fetch + render.
            if (!photoReady) { photo = await BuildSnapshotAsync(evt, ct); photoReady = true; }

            var sideEmoji = evt.Side == "Bid" ? "🟢" : "🔴";
            var sideWord = evt.Side == "Bid" ? "BUY" : "SELL";
            var text =
                $"🧱 *{evt.Symbol}* — {sideEmoji} *{sideWord} wall* @ {FmtPrice(evt.Price)} USDT\n" +
                $"💰 Notional: *{FmtUsdt(evt.QuoteNotional)}* · {evt.DistanceFromMidPercent:F2}% from mid\n" +
                $"📊 {evt.Multiplier:F1}× avg level · Quantity: {FmtBig(evt.Quantity)}\n" +
                $"⏱ {evt.At:HH:mm:ss} UTC";

            try
            {
                using var client = httpClientFactory.CreateClient("telegram");
                client.Timeout = TimeSpan.FromSeconds(12);
                using var resp = photo is not null
                    ? await SendPhotoAsync(client, u.BotToken, u.ChatId, photo, text, ct)
                    : await client.PostAsJsonAsync(
                        $"https://api.telegram.org/bot{u.BotToken}/sendMessage",
                        new { chat_id = u.ChatId, text, parse_mode = "Markdown" }, ct);
                if (!resp.IsSuccessStatusCode)
                    logger.LogWarning("Telegram wall alert send failed: {Status}", resp.StatusCode);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Wall alert HTTP send failed for {Sym}/{Side}", evt.Symbol, evt.Side);
            }
        }
    }

    /// <summary>Fetch the current order book (via the gated REST client) and render the wall ladder PNG.</summary>
    private async Task<byte[]?> BuildSnapshotAsync(OrderBookWallEvent evt, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var rest = scope.ServiceProvider.GetRequiredService<BinanceRestClient>();
            var depth = await rest.GetDepthAsync(evt.Symbol, 20, ct);
            if (depth is null) return null;
            return WallSnapshotRenderer.TryRender(evt.Symbol, evt.Side, evt.Price, evt.MidPrice, depth);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Wall snapshot build failed for {Sym} — falling back to text alert", evt.Symbol);
            return null;
        }
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
        form.Add(photo, "photo", "wall.png");
        return await client.PostAsync($"https://api.telegram.org/bot{token}/sendPhoto", form, ct);
    }

    private static string FmtBig(decimal n)
    {
        if (n >= 1_000_000m) return $"{n / 1_000_000m:F2}M";
        if (n >= 1_000m) return $"{n / 1_000m:F2}K";
        return n.ToString("F2");
    }
    private static string FmtUsdt(decimal n)
    {
        if (n >= 1_000_000_000m) return $"${n / 1_000_000_000m:F2}B";
        if (n >= 1_000_000m) return $"${n / 1_000_000m:F2}M";
        if (n >= 1_000m) return $"${n / 1_000m:F2}K";
        return $"${n:F0}";
    }
    private static string FmtPrice(decimal p)
    {
        if (p >= 1000) return p.ToString("N2");
        if (p >= 1) return p.ToString("F4");
        return p.ToString("G6");
    }

    /// <summary>Snapshot of a user's wall-alert settings, cached in-memory (refreshed on a timer).</summary>
    private sealed record CachedUser(
        Guid UserId,
        string BotToken,
        string ChatId,
        decimal MinNotional,
        decimal MaxDistancePct,
        string? Side,
        int CooldownMinutes);
}

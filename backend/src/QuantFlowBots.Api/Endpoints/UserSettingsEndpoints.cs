using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using QuantFlowBots.Domain.Entities;
using QuantFlowBots.Infrastructure.Persistence;

namespace QuantFlowBots.Api.Endpoints;

public static class UserSettingsEndpoints
{
    public sealed record UserSettingsDto(
        bool TelegramAlertsEnabled,
        bool TelegramBotTokenConfigured,
        string? TelegramChatId,
        bool WhaleAlertEnabled,
        bool WhaleAlertBotTokenConfigured,
        string? WhaleAlertChatId,
        string? WhaleAlertIntervals,
        decimal WhaleAlertMultiplier,
        decimal WhaleAlertMinVolume24h,
        string WhaleAlertMode,
        int WhaleAlertCooldownMinutes,
        string WhaleAlertDirection,
        int WhaleAlertLookback,
        bool WallAlertEnabled,
        bool WallAlertBotTokenConfigured,
        string? WallAlertChatId,
        decimal WallAlertMinNotional,
        decimal WallAlertMaxDistancePct,
        string WallAlertSide,
        int WallAlertCooldownMinutes,
        DateTimeOffset UpdatedAt);

    public sealed record UpdateUserSettingsRequest(
        bool? TelegramAlertsEnabled,
        string? TelegramBotToken,
        string? TelegramChatId,
        bool? WhaleAlertEnabled,
        string? WhaleAlertBotToken,
        string? WhaleAlertChatId,
        string? WhaleAlertIntervals,
        decimal? WhaleAlertMultiplier,
        decimal? WhaleAlertMinVolume24h,
        string? WhaleAlertMode,
        int? WhaleAlertCooldownMinutes,
        string? WhaleAlertDirection,
        int? WhaleAlertLookback,
        bool? WallAlertEnabled,
        string? WallAlertBotToken,
        string? WallAlertChatId,
        decimal? WallAlertMinNotional,
        decimal? WallAlertMaxDistancePct,
        string? WallAlertSide,
        int? WallAlertCooldownMinutes);

    public static IEndpointRouteBuilder MapUserSettings(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/me/settings").WithTags("settings").RequireAuthorization();

        grp.MapGet("/", async (QuantFlowBotsDbContext db, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var userId = ParseUserId(user);
            var s = await db.UserSettings.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId, ct);
            return Results.Ok(ToDto(s));
        });

        grp.MapPut("/", async (UpdateUserSettingsRequest req, QuantFlowBotsDbContext db, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var userId = ParseUserId(user);
            var s = await db.UserSettings.FirstOrDefaultAsync(x => x.UserId == userId, ct);
            if (s is null)
            {
                s = new UserSettings { UserId = userId };
                db.UserSettings.Add(s);
            }
            if (req.TelegramAlertsEnabled.HasValue) s.TelegramAlertsEnabled = req.TelegramAlertsEnabled.Value;
            if (req.TelegramBotToken is not null) s.TelegramBotToken = string.IsNullOrWhiteSpace(req.TelegramBotToken) ? null : req.TelegramBotToken.Trim();
            if (req.TelegramChatId is not null) s.TelegramChatId = string.IsNullOrWhiteSpace(req.TelegramChatId) ? null : req.TelegramChatId.Trim();

            if (req.WhaleAlertEnabled.HasValue) s.WhaleAlertEnabled = req.WhaleAlertEnabled.Value;
            if (req.WhaleAlertBotToken is not null) s.WhaleAlertBotToken = string.IsNullOrWhiteSpace(req.WhaleAlertBotToken) ? null : req.WhaleAlertBotToken.Trim();
            if (req.WhaleAlertChatId is not null) s.WhaleAlertChatId = string.IsNullOrWhiteSpace(req.WhaleAlertChatId) ? null : req.WhaleAlertChatId.Trim();
            if (req.WhaleAlertIntervals is not null) s.WhaleAlertIntervals = NormalizeIntervals(req.WhaleAlertIntervals);
            if (req.WhaleAlertMultiplier.HasValue) s.WhaleAlertMultiplier = Math.Clamp(req.WhaleAlertMultiplier.Value, 2m, 50m);
            if (req.WhaleAlertMinVolume24h.HasValue) s.WhaleAlertMinVolume24h = Math.Max(0m, req.WhaleAlertMinVolume24h.Value);
            if (req.WhaleAlertMode is not null && (req.WhaleAlertMode is "intrabar" or "candle_close")) s.WhaleAlertMode = req.WhaleAlertMode;
            if (req.WhaleAlertCooldownMinutes.HasValue) s.WhaleAlertCooldownMinutes = Math.Clamp(req.WhaleAlertCooldownMinutes.Value, 5, 1440);
            if (req.WhaleAlertDirection is not null && (req.WhaleAlertDirection is "buy" or "sell" or "both")) s.WhaleAlertDirection = req.WhaleAlertDirection;
            if (req.WhaleAlertLookback.HasValue) s.WhaleAlertLookback = Math.Clamp(req.WhaleAlertLookback.Value, 5, 50);

            if (req.WallAlertEnabled.HasValue) s.WallAlertEnabled = req.WallAlertEnabled.Value;
            if (req.WallAlertBotToken is not null) s.WallAlertBotToken = string.IsNullOrWhiteSpace(req.WallAlertBotToken) ? null : req.WallAlertBotToken.Trim();
            if (req.WallAlertChatId is not null) s.WallAlertChatId = string.IsNullOrWhiteSpace(req.WallAlertChatId) ? null : req.WallAlertChatId.Trim();
            if (req.WallAlertMinNotional.HasValue) s.WallAlertMinNotional = Math.Max(0m, req.WallAlertMinNotional.Value);
            if (req.WallAlertMaxDistancePct.HasValue) s.WallAlertMaxDistancePct = Math.Clamp(req.WallAlertMaxDistancePct.Value, 0m, 20m);
            if (req.WallAlertSide is not null && (req.WallAlertSide is "" or "Bid" or "Ask")) s.WallAlertSide = req.WallAlertSide;
            if (req.WallAlertCooldownMinutes.HasValue) s.WallAlertCooldownMinutes = Math.Clamp(req.WallAlertCooldownMinutes.Value, 1, 1440);

            s.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(s));
        });

        grp.MapPost("/telegram/test", (QuantFlowBotsDbContext db, IHttpClientFactory httpFactory, ClaimsPrincipal user, CancellationToken ct)
            => SendTestAsync(db, httpFactory, user, ct, useWhaleChannel: false));

        grp.MapPost("/wall-alerts/test", async (QuantFlowBotsDbContext db, IHttpClientFactory httpFactory, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var userId = ParseUserId(user);
            var s = await db.UserSettings.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId, ct);
            if (s is null || string.IsNullOrWhiteSpace(s.WallAlertBotToken) || string.IsNullOrWhiteSpace(s.WallAlertChatId))
                return Results.BadRequest(new { error = "Wall alerts Telegram not configured" });
            var text = "🧱 *Wall Alert TEST*\n" +
                       "_Sample format you'd receive on a real detection:_\n\n" +
                       "🟢 *BTCUSDT* — BUY wall @76,710 USDT\n" +
                       $"💰 Notional: $647.72K · 0.00% from mid\n" +
                       $"📏 ≥ ${FmtUsdt(s.WallAlertMinNotional)} · |Δ| ≤ {s.WallAlertMaxDistancePct}% · cooldown {s.WallAlertCooldownMinutes}m";
            try
            {
                using var client = httpFactory.CreateClient("telegram");
                client.Timeout = TimeSpan.FromSeconds(8);
                using var resp = await client.PostAsJsonAsync(
                    $"https://api.telegram.org/bot{s.WallAlertBotToken}/sendMessage",
                    new { chat_id = s.WallAlertChatId, text, parse_mode = "Markdown" }, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode) return Results.BadRequest(new { error = $"Telegram {(int)resp.StatusCode}", body });
                return Results.Ok(new { sent = true });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        grp.MapPost("/whale-alerts/test", async (
            string? symbol,
            QuantFlowBotsDbContext db,
            IHttpClientFactory httpFactory,
            QuantFlowBots.Infrastructure.Exchanges.Binance.BinanceRestClient binance,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            var userId = ParseUserId(user);
            var s = await db.UserSettings.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId, ct);
            if (s is null || string.IsNullOrWhiteSpace(s.WhaleAlertBotToken) || string.IsNullOrWhiteSpace(s.WhaleAlertChatId))
                return Results.BadRequest(new { error = "Whale alerts Telegram not configured" });

            var sym = (symbol ?? "BTCUSDT").ToUpperInvariant();
            var firstInterval = (s.WhaleAlertIntervals ?? "15m")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? "15m";
            var ivEnumName = firstInterval switch
            {
                "1m" => "OneMinute", "3m" => "ThreeMinutes", "5m" => "FiveMinutes",
                "15m" => "FifteenMinutes", "30m" => "ThirtyMinutes",
                "1h" => "OneHour", "2h" => "TwoHours", "4h" => "FourHours", _ => "FifteenMinutes",
            };
            if (!Enum.TryParse<QuantFlowBots.Domain.Enums.CandleInterval>(ivEnumName, true, out var iv))
                iv = QuantFlowBots.Domain.Enums.CandleInterval.FifteenMinutes;

            var lookback = Math.Clamp(s.WhaleAlertLookback, 5, 50);
            var candles = await binance.GetCandlesAsync(sym, iv, null, null, lookback + 1, ct);
            if (candles.Count < 3) return Results.BadRequest(new { error = $"Not enough klines for {sym}" });
            var curr = candles[^1];
            decimal sum = 0m; var n = 0;
            for (var i = candles.Count - 1 - lookback; i < candles.Count - 1; i++) { if (i < 0) continue; sum += candles[i].QuoteVolume; n++; }
            var avg = n == 0 ? 0m : sum / n;
            var ratio = avg > 0m ? curr.QuoteVolume / avg : 0m;
            var direction = curr.Close >= curr.Open ? "buy" : "sell";
            var baseAsset = sym.EndsWith("USDT", StringComparison.OrdinalIgnoreCase) ? sym[..^4] : sym;
            var pricePct = curr.Open > 0 ? (curr.Close - curr.Open) / curr.Open * 100m : 0m;
            decimal vol24h = 0m;
            // Quick 24h vol read — sum of last 96 (or however many) bars.
            foreach (var c in candles) vol24h += c.QuoteVolume;

            var text =
                $"🐋 *{sym}* `[Binance]` _(TEST PREVIEW)_\n" +
                $"{(direction == "buy" ? "🟢" : "🔴")} Big Whales *{(direction == "buy" ? "Buy" : "Sell")}* Activity\n" +
                $"{FmtBig(curr.Volume)} {baseAsset} have been {(direction == "buy" ? "bought" : "sold")}\n\n" +
                $"💰 Price: {FmtPrice(curr.Close)} USDT (*{(pricePct >= 0 ? "+" : "")}{pricePct:F2}%*)\n" +
                $"🚨 Order Size: {FmtUsdt(curr.QuoteVolume)} USDT (*{ratio:F2}×* avg of last {n} × {firstInterval})\n" +
                $"📈 Avg baseline: {FmtUsdt(avg)} USDT\n" +
                $"⌛ Window: *{firstInterval}*\n" +
                $"📊 Recent vol (sum {candles.Count}× {firstInterval}): {FmtUsdt(vol24h)} USDT";

            try
            {
                using var client = httpFactory.CreateClient("telegram");
                client.Timeout = TimeSpan.FromSeconds(8);
                using var resp = await client.PostAsJsonAsync(
                    $"https://api.telegram.org/bot{s.WhaleAlertBotToken}/sendMessage",
                    new { chat_id = s.WhaleAlertChatId, text, parse_mode = "Markdown" }, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode) return Results.BadRequest(new { error = $"Telegram {(int)resp.StatusCode}", body });
                return Results.Ok(new { sent = true, symbol = sym, interval = firstInterval, ratio = Math.Round(ratio, 2), direction });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        return app;
    }

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

    private static async Task<IResult> SendTestAsync(QuantFlowBotsDbContext db, IHttpClientFactory httpFactory,
        ClaimsPrincipal user, CancellationToken ct, bool useWhaleChannel)
    {
        var userId = ParseUserId(user);
        var s = await db.UserSettings.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId, ct);
        var token = useWhaleChannel ? s?.WhaleAlertBotToken : s?.TelegramBotToken;
        var chatId = useWhaleChannel ? s?.WhaleAlertChatId : s?.TelegramChatId;
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(chatId))
            return Results.BadRequest(new { error = useWhaleChannel ? "Whale alerts Telegram not configured" : "Telegram not configured" });
        try
        {
            using var client = httpFactory.CreateClient("telegram");
            client.Timeout = TimeSpan.FromSeconds(8);
            var text = useWhaleChannel
                ? "🐋 Quant Flow Bots — Whale Alerts test message"
                : "🤖 Quant Flow Bots — test message from settings";
            using var resp = await client.PostAsJsonAsync(
                $"https://api.telegram.org/bot{token}/sendMessage", new { chat_id = chatId, text }, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) return Results.BadRequest(new { error = $"Telegram {(int)resp.StatusCode}", body });
            return Results.Ok(new { sent = true });
        }
        catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
    }

    private static string NormalizeIntervals(string raw)
    {
        var allowed = new HashSet<string>(["1m","3m","5m","15m","30m","1h","2h","4h"], StringComparer.OrdinalIgnoreCase);
        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant())
            .Where(allowed.Contains)
            .Distinct();
        return string.Join(',', parts);
    }

    private static UserSettingsDto ToDto(UserSettings? s) => s is null
        ? new UserSettingsDto(false, false, null, false, false, null, null, 5m, 500_000m, "candle_close", 60, "both", 20,
            false, false, null, 500_000m, 2m, "", 30, DateTimeOffset.MinValue)
        : new UserSettingsDto(
            s.TelegramAlertsEnabled, !string.IsNullOrWhiteSpace(s.TelegramBotToken), s.TelegramChatId,
            s.WhaleAlertEnabled, !string.IsNullOrWhiteSpace(s.WhaleAlertBotToken), s.WhaleAlertChatId,
            s.WhaleAlertIntervals, s.WhaleAlertMultiplier, s.WhaleAlertMinVolume24h,
            s.WhaleAlertMode ?? "candle_close", s.WhaleAlertCooldownMinutes, s.WhaleAlertDirection ?? "both",
            s.WhaleAlertLookback,
            s.WallAlertEnabled, !string.IsNullOrWhiteSpace(s.WallAlertBotToken), s.WallAlertChatId,
            s.WallAlertMinNotional, s.WallAlertMaxDistancePct, s.WallAlertSide ?? "", s.WallAlertCooldownMinutes,
            s.UpdatedAt);

    private static Guid ParseUserId(ClaimsPrincipal user)
        => Guid.TryParse(user.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub), out var id) ? id : Guid.Empty;
}

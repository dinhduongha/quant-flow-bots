using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QuantFlowBots.Infrastructure.Persistence;
using StackExchange.Redis;

namespace QuantFlowBots.Infrastructure.Exchanges.Binance;

/// <summary>
/// Redis-backed implementation of <see cref="IBinanceGate"/>.
///
/// Keys:
///   binance:gate           — hash: state, until, statusCode, reason, lastEndpoint, source,
///                                   retryAfter, firstOpenedAt
///   binance:gate:opens24h  — counter, TTL 24h, used to escalate cooldown for repeat offenders
///
/// Cooldown policy:
///   - 429 with Retry-After header → honor that exact value (min 30s)
///   - 429 without Retry-After     → 60s
///   - 418 (IP ban)                → 5min / 15min / 30min by openCount24h
/// </summary>
public sealed class RedisBinanceGate(
    IConnectionMultiplexer redis,
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    ILogger<RedisBinanceGate> logger) : IBinanceGate
{
    private const string GateKey = "binance:gate";
    private const string OpensCounterKey = "binance:gate:opens24h";
    private readonly string _source = Environment.GetEnvironmentVariable("QFB_PROCESS") ?? AppDomain.CurrentDomain.FriendlyName;

    public async Task EnsureClosedAsync(string endpoint, CancellationToken cancellationToken)
    {
        var state = await GetStateAsync(cancellationToken);
        if (state.IsOpen) throw new BinanceGateOpenException(state);
    }

    public async Task<BinanceGateState> GetStateAsync(CancellationToken cancellationToken)
    {
        var db = redis.GetDatabase();
        var hash = await db.HashGetAllAsync(GateKey);
        if (hash.Length == 0) return new BinanceGateState(false, null, null, null, null, 0);

        var dict = hash.ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
        var until = dict.TryGetValue("until", out var u) && DateTimeOffset.TryParse(u, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : (DateTimeOffset?)null;
        var isOpen = until.HasValue && until.Value > DateTimeOffset.UtcNow;
        if (!isOpen && until.HasValue)
        {
            // Auto-recover: TTL elapsed — clear stale metadata so next failure starts fresh.
            await db.KeyDeleteAsync(GateKey);
            return new BinanceGateState(false, null, null, null, null, await GetOpensCounterAsync(db));
        }

        return new BinanceGateState(
            IsOpen: isOpen,
            Until: until,
            StatusCode: dict.TryGetValue("statusCode", out var sc) && int.TryParse(sc, out var iSc) ? iSc : null,
            Reason: dict.TryGetValue("reason", out var r) ? r : null,
            LastEndpoint: dict.TryGetValue("lastEndpoint", out var le) ? le : null,
            OpenCount24h: await GetOpensCounterAsync(db));
    }

    public Task ReportSuccessAsync(string endpoint, CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task ReportFailureAsync(string endpoint, int statusCode, int? retryAfterSeconds, CancellationToken cancellationToken)
    {
        if (statusCode is not (418 or 429)) return;

        var db = redis.GetDatabase();
        var openCount = (int)await db.StringIncrementAsync(OpensCounterKey);
        if (openCount == 1) await db.KeyExpireAsync(OpensCounterKey, TimeSpan.FromHours(24));

        TimeSpan cooldown;
        string reason;
        if (statusCode == 429)
        {
            cooldown = TimeSpan.FromSeconds(Math.Max(retryAfterSeconds ?? 60, 30));
            reason = "RateLimit429";
        }
        else
        {
            cooldown = openCount switch { 1 => TimeSpan.FromMinutes(5), 2 => TimeSpan.FromMinutes(15), _ => TimeSpan.FromMinutes(30) };
            reason = "IpBanned418";
        }

        var until = DateTimeOffset.UtcNow.Add(cooldown);
        var alreadyOpen = (await db.HashGetAsync(GateKey, "until")).HasValue;

        await db.HashSetAsync(GateKey, new HashEntry[]
        {
            new("state", "open"),
            new("until", until.ToString("O", CultureInfo.InvariantCulture)),
            new("statusCode", statusCode),
            new("reason", reason),
            new("lastEndpoint", endpoint),
            new("source", _source),
            new("retryAfter", retryAfterSeconds ?? -1),
            new("firstOpenedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)),
        });
        await db.KeyExpireAsync(GateKey, cooldown + TimeSpan.FromMinutes(1));

        logger.LogError("Binance gate OPEN: status={Status} reason={Reason} cooldown={Cooldown} until={Until} endpoint={Endpoint} openCount24h={N}",
            statusCode, reason, cooldown, until, endpoint, openCount);

        if (!alreadyOpen) _ = TryNotifyTelegramAsync(statusCode, reason, until, endpoint, openCount);
    }

    private async Task<int> GetOpensCounterAsync(IDatabase db)
    {
        var v = await db.StringGetAsync(OpensCounterKey);
        // Explicit string cast — .NET 10 added int.TryParse(ReadOnlySpan<byte>, …) which collides
        // with RedisValue's dual implicit conversion (string + byte span) and the overload is ambiguous.
        return v.HasValue && int.TryParse((string?)v, out var n) ? n : 0;
    }

    private async Task TryNotifyTelegramAsync(int statusCode, string reason, DateTimeOffset until, string endpoint, int openCount)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<QuantFlowBotsDbContext>();
            var recipients = db.Set<Domain.Entities.UserSettings>()
                .AsNoTracking()
                .Where(s => s.TelegramAlertsEnabled && s.TelegramBotToken != null && s.TelegramChatId != null)
                .Select(s => new { s.TelegramBotToken, s.TelegramChatId })
                .ToList();
            if (recipients.Count == 0) return;

            using var client = httpClientFactory.CreateClient("telegram");
            client.Timeout = TimeSpan.FromSeconds(5);
            var text = $"🚨 *Binance gate OPEN*\nstatus={statusCode} reason={reason}\nendpoint=`{endpoint}`\nuntil {until:HH:mm:ss} (UTC)\nopenCount24h={openCount} source={_source}";
            foreach (var r in recipients)
            {
                try
                {
                    await client.PostAsync($"https://api.telegram.org/bot{r.TelegramBotToken}/sendMessage", System.Net.Http.Json.JsonContent.Create(new { chat_id = r.TelegramChatId, text, parse_mode = "Markdown" }));
                }
                catch { /* one user's misconfigured bot must not block the rest */ }
            }
        }
        catch (Exception ex) { logger.LogWarning(ex, "Failed to send Telegram alert for gate open"); }
    }
}

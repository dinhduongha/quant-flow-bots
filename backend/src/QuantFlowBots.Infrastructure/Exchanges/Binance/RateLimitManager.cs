using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace QuantFlowBots.Infrastructure.Exchanges.Binance;

/// <summary>
/// Proactive Binance REST weight governor — the companion to <see cref="IBinanceGate"/>.
///
/// The gate is REACTIVE: it only trips after Binance returns 418/429. By then we're already
/// throttled or banned. RateLimitManager is PROACTIVE: it reads the <c>X-MBX-USED-WEIGHT-1M</c>
/// header Binance stamps on every response and steers traffic BEFORE we hit a hard limit:
///   - &lt; SlowDown%       → Allow
///   - SlowDown%..Critical% → Delay (the deeper in, the longer) — protects the budget
///   - ≥ Critical%          → Reject non-critical (market data) requests; trading still flows
///
/// State lives in Redis because API and Worker are separate processes sharing ONE IP — and the
/// weight budget is per-IP. The header value reflects the IP's total used weight in the current
/// minute, so whichever process called most recently holds the freshest reading for both.
/// </summary>
public sealed class RateLimitManager(
    IConnectionMultiplexer redis,
    IOptions<BinanceOptions> options,
    ILogger<RateLimitManager> logger)
{
    private const string Key = "binance:weight";
    private readonly BinanceOptions _opt = options.Value;
    private readonly string _source = Environment.GetEnvironmentVariable("QFB_PROCESS") ?? AppDomain.CurrentDomain.FriendlyName;

    /// <summary>POST-response: persist the weight Binance reported for the current 1-min window.</summary>
    public async Task RecordUsedWeightAsync(int usedWeight, string endpoint, CancellationToken ct)
    {
        try
        {
            var db = redis.GetDatabase();
            await db.HashSetAsync(Key,
            [
                new HashEntry("usedWeight", usedWeight),
                new HashEntry("limit", _opt.WeightLimitPerMinute),
                new HashEntry("observedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)),
                new HashEntry("lastEndpoint", endpoint),
                new HashEntry("source", _source),
            ]);
            // Binance resets the 1-min window each minute; let the key self-clear if traffic stops
            // so a stale high reading can't keep us throttled after we've gone quiet.
            await db.KeyExpireAsync(Key, TimeSpan.FromMinutes(2));
        }
        catch (Exception ex) { logger.LogDebug(ex, "RateLimitManager: failed recording weight"); }
    }

    /// <summary>PRE-request: decide whether to allow, delay, or reject based on current weight.</summary>
    public async Task<RateLimitDecision> EvaluateAsync(bool isCritical, CancellationToken ct)
    {
        var snap = await GetSnapshotAsync(ct);
        var pct = snap.UsedPercent;

        if (pct >= _opt.WeightCriticalOnlyPercent && !isCritical)
            return RateLimitDecision.Reject(snap);

        if (pct >= _opt.WeightSlowDownPercent)
        {
            // Ramp the delay from 0 at SlowDown% to ~2s as we approach the limit. Trading
            // (critical) gets a smaller cap so order latency stays acceptable under pressure.
            var span = Math.Max(1, 100 - _opt.WeightSlowDownPercent);
            var over = Math.Clamp((pct - _opt.WeightSlowDownPercent) / (double)span, 0, 1);
            var capMs = isCritical ? 400 : 2000;
            return RateLimitDecision.Wait(TimeSpan.FromMilliseconds(over * capMs), snap);
        }

        return RateLimitDecision.Allow(snap);
    }

    public async Task<RateLimitSnapshot> GetSnapshotAsync(CancellationToken ct)
    {
        var limit = _opt.WeightLimitPerMinute;
        try
        {
            var db = redis.GetDatabase();
            var hash = await db.HashGetAllAsync(Key);
            if (hash.Length == 0)
                return new RateLimitSnapshot(0, limit, 0, _opt.WeightSlowDownPercent, _opt.WeightCriticalOnlyPercent, null, null, null);

            var dict = hash.ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
            var used = dict.TryGetValue("usedWeight", out var w) && int.TryParse(w, out var iw) ? iw : 0;
            var storedLimit = dict.TryGetValue("limit", out var l) && int.TryParse(l, out var il) && il > 0 ? il : limit;
            var observedAt = dict.TryGetValue("observedAt", out var o) &&
                DateTimeOffset.TryParse(o, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : (DateTimeOffset?)null;
            var pct = storedLimit > 0 ? (int)Math.Round(used * 100.0 / storedLimit) : 0;
            return new RateLimitSnapshot(
                used, storedLimit, pct, _opt.WeightSlowDownPercent, _opt.WeightCriticalOnlyPercent,
                observedAt,
                dict.TryGetValue("lastEndpoint", out var le) ? le : null,
                dict.TryGetValue("source", out var s) ? s : null);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "RateLimitManager: failed reading snapshot");
            return new RateLimitSnapshot(0, limit, 0, _opt.WeightSlowDownPercent, _opt.WeightCriticalOnlyPercent, null, null, null);
        }
    }

    /// <summary>
    /// Heuristic: trading/account endpoints are CRITICAL (never starve an order to save weight);
    /// everything else (klines/depth/ticker/exchangeInfo) is market data and may be throttled.
    /// </summary>
    public static bool IsCriticalEndpoint(string path) =>
        path.Contains("/order", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("/account", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("/openOrders", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("/allOrders", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("/positionRisk", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("/leverage", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("/marginType", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("/userDataStream", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("/listenKey", StringComparison.OrdinalIgnoreCase);
}

public enum RateLimitAction { Allow, Wait, Reject }

public sealed record RateLimitDecision(RateLimitAction Action, TimeSpan Delay, RateLimitSnapshot Snapshot)
{
    public static RateLimitDecision Allow(RateLimitSnapshot s) => new(RateLimitAction.Allow, TimeSpan.Zero, s);
    public static RateLimitDecision Wait(TimeSpan d, RateLimitSnapshot s) => new(RateLimitAction.Wait, d, s);
    public static RateLimitDecision Reject(RateLimitSnapshot s) => new(RateLimitAction.Reject, TimeSpan.Zero, s);
}

public sealed record RateLimitSnapshot(
    int UsedWeight,
    int Limit,
    int UsedPercent,
    int SlowDownPercent,
    int CriticalOnlyPercent,
    DateTimeOffset? ObservedAt,
    string? LastEndpoint,
    string? Source);

/// <summary>
/// Thrown when a non-critical request is rejected for weight reasons. Derives from
/// <see cref="HttpRequestException"/> so existing graceful handlers (e.g. TickerSnapshotCache
/// serving its last good snapshot) treat it like any transient REST failure.
/// </summary>
public sealed class RateLimitThrottledException(RateLimitSnapshot snapshot)
    : HttpRequestException($"Binance request rejected to protect weight budget ({snapshot.UsedPercent}% of {snapshot.Limit}/min used).")
{
    public RateLimitSnapshot Snapshot { get; } = snapshot;
}

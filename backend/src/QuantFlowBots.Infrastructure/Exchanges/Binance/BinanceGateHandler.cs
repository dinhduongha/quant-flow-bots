using Microsoft.Extensions.Logging;

namespace QuantFlowBots.Infrastructure.Exchanges.Binance;

/// <summary>
/// DelegatingHandler that wraps every outbound Binance REST call — the single chokepoint where
/// all weight control happens (BinanceApiClient → [this] → REST API). Flow:
///   - PRE: refuse the call if the shared gate is open (418/429 cooldown).
///   - PRE: ask <see cref="RateLimitManager"/> — at ≥70% used weight delay the call; at ≥85%
///          reject non-critical (market-data) calls so trading keeps its budget.
///   - PRE: increment per-endpoint call counter for observability.
///   - POST: record the X-MBX-USED-WEIGHT-1M header; on 418/429 → report failure.
///
/// Attached to BinanceRestClient / BinanceFuturesRestClient / BinanceSpotSignedClient via
/// .AddHttpMessageHandler in DI — no per-method instrumentation needed.
/// </summary>
public sealed class BinanceGateHandler(
    IBinanceGate gate,
    RateLimitManager rateLimit,
    BinanceCallCounter counter,
    ILogger<BinanceGateHandler> logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var endpoint = request.RequestUri?.AbsolutePath ?? "(unknown)";
        await gate.EnsureClosedAsync(endpoint, cancellationToken);

        // Proactive weight governance, BEFORE the call goes out.
        var isCritical = RateLimitManager.IsCriticalEndpoint(endpoint);
        var decision = await rateLimit.EvaluateAsync(isCritical, cancellationToken);
        if (decision.Action == RateLimitAction.Reject)
        {
            logger.LogWarning("Weight {Pct}% ≥ critical — skipping non-critical {Endpoint}", decision.Snapshot.UsedPercent, endpoint);
            throw new RateLimitThrottledException(decision.Snapshot);
        }
        if (decision.Action == RateLimitAction.Wait && decision.Delay > TimeSpan.Zero)
            await Task.Delay(decision.Delay, cancellationToken);

        counter.Increment(endpoint);

        var response = await base.SendAsync(request, cancellationToken);

        // Record the IP's used weight for the current minute (shared across API + Worker via Redis).
        if (response.Headers.TryGetValues("X-MBX-USED-WEIGHT-1M", out var wv) &&
            int.TryParse(wv.FirstOrDefault(), out var usedWeight))
        {
            await rateLimit.RecordUsedWeightAsync(usedWeight, endpoint, cancellationToken);
        }

        var sc = (int)response.StatusCode;
        if (sc is 418 or 429)
        {
            int? retryAfter = null;
            if (response.Headers.RetryAfter?.Delta is { } d) retryAfter = (int)d.TotalSeconds;
            else if (response.Headers.TryGetValues("Retry-After", out var v) && int.TryParse(v.FirstOrDefault(), out var s)) retryAfter = s;
            logger.LogWarning("Binance returned {Status} for {Endpoint} retryAfter={RetryAfter}", sc, endpoint, retryAfter);
            await gate.ReportFailureAsync(endpoint, sc, retryAfter, cancellationToken);
        }

        return response;
    }
}

using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using QuantFlowBots.Application.Exchanges;
using QuantFlowBots.Domain.Enums;
using QuantFlowBots.Infrastructure.Exchanges.Binance;
using QuantFlowBots.Infrastructure.Persistence;
using QuantFlowBots.Infrastructure.Trading;

namespace QuantFlowBots.Api.Endpoints;

public static class MarketEndpoints
{
    public static IEndpointRouteBuilder MapMarket(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/market").WithTags("market");

        grp.MapGet("/overview", async (TickerSnapshotCache tickerCache, CancellationToken ct) =>
        {
            var tickers = await tickerCache.GetAsync(ct);
            // Base filter: USDT pairs, ASCII-clean, not a stable/wrapped-USD peg.
            var usdt = tickers
                .Where(t => t.Symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase) && t.QuoteVolume > 0)
                .Where(t => t.Symbol.All(c => c < 128 && (char.IsLetterOrDigit(c) || c == '_')))
                .Where(t => !StableBaseAssets.Contains(t.Symbol[..^4]))      // explicit stable list
                .Where(t => Math.Abs(t.LastPrice - 1m) > 0.03m)              // dynamic peg safety net
                .ToList();

            // Top gainer needs min liquidity — Binance keeps returning ticker entries for delisted
            // symbols with stale +60% PCT but ~0 trades; this gate drops those ghosts.
            const decimal topGainerMinQuoteVolume = 5_000_000m;
            var topGainers = usdt
                .Where(t => t.QuoteVolume >= topGainerMinQuoteVolume)
                .OrderByDescending(t => t.PriceChangePercent).Take(10);

            return Results.Ok(new
            {
                updatedAt = DateTimeOffset.UtcNow,
                topGainers,
                topVolume = usdt.OrderByDescending(t => t.QuoteVolume).Take(10),
            });
        });

        grp.MapGet("/account-stats", async (System.Security.Claims.ClaimsPrincipal user, QuantFlowBotsDbContext db, CancellationToken ct) =>
        {
            if (!Guid.TryParse(user.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value, out var userId))
                return Results.Unauthorized();
            var todayUtc = DateTimeOffset.UtcNow.Date;
            var openCount = await db.Positions
                .Where(p => p.Status == PositionStatus.Open && p.Bot!.UserId == userId)
                .CountAsync(ct);
            var todayPnl = await db.Positions
                .Where(p => p.Status == PositionStatus.Closed
                    && p.ClosedAt != null && p.ClosedAt >= todayUtc
                    && p.Bot!.UserId == userId)
                .SumAsync(p => (decimal?)p.RealizedPnl, ct) ?? 0m;
            return Results.Ok(new { openPositions = openCount, todayPnl });
        }).RequireAuthorization();

        grp.MapGet("/new-listings", async (int? limit, TickerSnapshotCache tickerCache, QuantFlowBotsDbContext db, CancellationToken ct) =>
        {
            var take = Math.Clamp(limit ?? 5, 1, 20);
            var newest = await db.Symbols
                .Where(s => s.IsActive && s.ListedAt != null && s.QuoteAsset == "USDT")
                .OrderByDescending(s => s.ListedAt)
                .Take(take * 4)
                .Select(s => new { s.Code, s.BaseAsset, s.ListedAt })
                .ToListAsync(ct);
            if (newest.Count == 0) return Results.Ok(Array.Empty<object>());

            var tickers = await tickerCache.GetAsync(ct);
            var byCode = tickers.ToDictionary(t => t.Symbol, StringComparer.OrdinalIgnoreCase);

            var result = newest
                .Select(s =>
                {
                    byCode.TryGetValue(s.Code, out var t);
                    return new
                    {
                        code = s.Code,
                        baseAsset = s.BaseAsset,
                        listedAt = s.ListedAt,
                        price = t?.LastPrice ?? 0m,
                        priceChangePercent = t?.PriceChangePercent ?? 0m,
                        quoteVolume = t?.QuoteVolume ?? 0m,
                    };
                })
                .Where(r => r.price > 0)
                .Take(take)
                .ToList();
            return Results.Ok(result);
        });

        // alternative.me Fear & Greed Index. Their data updates ~once/day at 00:00 UTC so we
        // cache aggressively (1h) to avoid hammering and to survive transient upstream errors.
        grp.MapGet("/fear-greed", async (FearGreedCache cache, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            var snapshot = await cache.GetOrFetchAsync(httpFactory, ct);
            return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
        });

        // Binance REST weight usage + circuit-breaker state for the dashboard "weight gauge".
        // Combines the proactive governor (RateLimitManager: used/limit/% + thresholds) with the
        // reactive gate (IBinanceGate: open/until/reason on 418/429). One IP, shared via Redis.
        grp.MapGet("/rate-limit", async (RateLimitManager rateLimit, IBinanceGate gate, CancellationToken ct) =>
        {
            var w = await rateLimit.GetSnapshotAsync(ct);
            var g = await gate.GetStateAsync(ct);

            var level = g.IsOpen ? "banned"
                : w.UsedPercent >= w.CriticalOnlyPercent ? "criticalOnly"
                : w.UsedPercent >= w.SlowDownPercent ? "slowDown"
                : "normal";

            // Binance resets the per-minute window on the clock minute boundary.
            var now = DateTimeOffset.UtcNow;
            var windowResetInSeconds = 60 - now.Second;

            return Results.Ok(new
            {
                usedWeight = w.UsedWeight,
                limit = w.Limit,
                usedPercent = w.UsedPercent,
                slowDownPercent = w.SlowDownPercent,
                criticalOnlyPercent = w.CriticalOnlyPercent,
                level,
                observedAt = w.ObservedAt,
                lastEndpoint = w.LastEndpoint,
                source = w.Source,
                windowResetInSeconds,
                gate = new
                {
                    isOpen = g.IsOpen,
                    until = g.Until,
                    statusCode = g.StatusCode,
                    reason = g.Reason,
                    openCount24h = g.OpenCount24h,
                },
            });
        });

        // Symbols flagged by BinanceAnnouncementWorker / SymbolStatusReconcilerWorker. Bots are
        // already blocked from dispatching on these in BotRuntime; the UI shows them for context.
        // Reads DB directly so the API's in-memory copy can never go stale relative to Worker writes.
        grp.MapGet("/risk-flags", async (QuantFlowBotsDbContext db, CancellationToken ct) =>
        {
            var rows = await db.SymbolRiskFlags.AsNoTracking()
                .OrderByDescending(r => r.At)
                .Select(r => new { symbol = r.Symbol, reason = r.Reason, source = r.Source, url = r.Url, at = r.At })
                .ToListAsync(ct);
            return Results.Ok(rows);
        });

        // Manual unblock — when user wants to revert a flag (false positive, retest after fix, etc).
        grp.MapDelete("/risk-flags/{symbol}", async (string symbol, QuantFlowBots.Application.Risk.SymbolRiskGate gate, CancellationToken ct) =>
        {
            var removed = await gate.UnblockAsync(symbol, ct);
            return removed ? Results.NoContent() : Results.NotFound();
        }).RequireAuthorization();

        grp.MapGet("/order-book-walls", (
            decimal? minNotional,
            decimal? maxDistancePct,
            string? side,
            int? limit,
            string? exclude,
            OrderBookWallCache cache,
            Microsoft.Extensions.Options.IOptions<OrderBookWallOptions> opts) =>
        {
            var min = minNotional ?? opts.Value.MinNotionalUsdt;
            var maxDist = maxDistancePct ?? opts.Value.MaxDistanceFromMidPercent;
            var take = Math.Clamp(limit ?? 25, 1, 200);
            var excludeSet = (exclude ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.ToUpperInvariant())
                .ToHashSet();
            var excludedStableBases = opts.Value.ExcludedBaseAssets
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var quoteFilter = opts.Value.QuoteAssets is { Length: > 0 } ? opts.Value.QuoteAssets : ["USDT"];
            var stableBand = opts.Value.StablePairPriceBandPercent;
            var snap = cache.Snapshot();
            var filtered = snap
                .Where(w => w.QuoteNotional >= min)
                .Where(w => w.DistanceFromMidPercent <= maxDist)
                .Where(w => string.IsNullOrEmpty(side) || string.Equals(w.Side, side, StringComparison.OrdinalIgnoreCase))
                .Where(w => !excludeSet.Contains(w.Symbol))
                .Where(w => !IsExcludedStablePair(w.Symbol, quoteFilter, excludedStableBases))
                // Dynamic stable-pair filter as safety net for entries already in the cache
                // before the worker picked up the new options.
                .Where(w => stableBand <= 0m || Math.Abs(w.MidPrice - 1m) / 1m * 100m > stableBand)
                .OrderByDescending(w => w.QuoteNotional)
                .Take(take)
                .ToList();
            return Results.Ok(new
            {
                updatedAt = DateTimeOffset.UtcNow,
                filter = new { minNotional = min, maxDistancePct = maxDist, side = side ?? "any", limit = take },
                defaults = new { minNotional = opts.Value.MinNotionalUsdt, detectionFloor = opts.Value.DetectionFloorUsdt, maxDistancePct = opts.Value.MaxDistanceFromMidPercent, scanIntervalSeconds = opts.Value.ScanIntervalSeconds, maxSymbols = opts.Value.MaxSymbols, excludedBaseAssets = opts.Value.ExcludedBaseAssets },
                totalCached = snap.Count,
                count = filtered.Count,
                results = filtered,
            });
        });

        grp.MapGet("/scanner", async (
            decimal? minVolume,
            decimal? minPct,
            decimal? maxPct,
            string? exclude,
            string? include,
            string? windowSize,
            string? direction,
            int? maxSymbols,
            IExchangeClient exchange,
            TickerSnapshotCache tickerCache,
            QuantFlowBotsDbContext db,
            CancellationToken ct) =>
        {
            var window = NormalizeWindowSize(windowSize);
            if (window is null)
                return Results.BadRequest(new { error = "invalid_window_size", allowed = "1m..59m, 1h..23h, 1d..7d" });

            var dir = (direction ?? "any").Trim().ToLowerInvariant();
            if (dir is not ("any" or "up" or "down"))
                return Results.BadRequest(new { error = "invalid_direction", allowed = "any | up | down" });

            var minVol = minVolume ?? 50_000_000m;
            var minP = minPct ?? 1m;
            var maxP = maxPct ?? 25m;
            var take = Math.Clamp(maxSymbols ?? 30, 1, 100);
            var blacklist = (exclude ?? "USDCUSDT,FDUSDUSDT,TUSDUSDT,BUSDUSDT,USDPUSDT,USDDUSDT,DAIUSDT,SUSDUSDT,USD1USDT,EURUSDT,EURIUSDT,AEURUSDT,EURTUSDT")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var whitelist = string.IsNullOrWhiteSpace(include)
                ? null
                : include.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var usdtSymbols = await db.Symbols
                .Where(s => s.IsActive && s.QuoteAsset == "USDT")
                .OrderBy(s => s.Code)
                .Select(s => s.Code)
                .ToListAsync(ct);

            if (whitelist is not null)
                usdtSymbols = usdtSymbols.Where(whitelist.Contains).ToList();

            // Pre-filter by 24h volume (single weight-80 call) to cap rolling-ticker fanout.
            // Calling /api/v3/ticker for 1000+ symbols burns 2000+ weight and 429s on Binance.
            var usdtSet = usdtSymbols.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var pre = await tickerCache.GetAsync(ct);
            var preTop = pre
                .Where(t => usdtSet.Contains(t.Symbol))
                .Where(t => t.QuoteVolume >= minVol)
                .OrderByDescending(t => t.QuoteVolume)
                .Take(200)
                .Select(t => t.Symbol)
                .ToList();

            var tickers = window == "1d"
                ? pre.Where(t => preTop.Contains(t.Symbol)).ToList()
                : (IReadOnlyList<TickerSnapshot>)await exchange.GetRollingTickersAsync(preTop, window, ct);
            var usdtPairs = tickers.ToList();
            var passVolume = usdtPairs.Where(t => t.QuoteVolume >= minVol).ToList();
            var passPct = passVolume
                .Where(t => dir switch
                {
                    "up"   => t.PriceChangePercent >=  minP && t.PriceChangePercent <=  maxP,
                    "down" => t.PriceChangePercent <= -minP && t.PriceChangePercent >= -maxP,
                    _      => Math.Abs(t.PriceChangePercent) >= minP && Math.Abs(t.PriceChangePercent) <= maxP,
                })
                .ToList();
            var passBlacklist = passPct.Where(t => !blacklist.Contains(t.Symbol)).ToList();
            var passWhitelist = passBlacklist.ToList();

            var matches = passWhitelist
                .OrderByDescending(t => t.QuoteVolume)
                .Take(take)
                .Select(t => new
                {
                    symbol = t.Symbol,
                    price = t.LastPrice,
                    priceChangePercent = t.PriceChangePercent,
                    quoteVolume = t.QuoteVolume,
                })
                .ToList();

            // Compute near-misses to help user loosen the filter
            object? nearMissPct = null;
            if (passPct.Count == 0 && passVolume.Count > 0)
            {
                // Order near-miss by direction so the hint matches what the user is searching for.
                var ordered = dir switch
                {
                    "up"   => passVolume.OrderByDescending(t => t.PriceChangePercent),
                    "down" => passVolume.OrderBy(t => t.PriceChangePercent),
                    _      => passVolume.OrderByDescending(t => Math.Abs(t.PriceChangePercent)),
                };
                var closest = ordered
                    .Take(5)
                    .Select(t => new { symbol = t.Symbol, priceChangePercent = t.PriceChangePercent, quoteVolume = t.QuoteVolume })
                    .ToList();
                nearMissPct = new { maxAbsPctSeen = closest.Count > 0 ? Math.Abs(closest[0].priceChangePercent) : 0m, samples = closest };
            }

            return Results.Ok(new
            {
                updatedAt = DateTimeOffset.UtcNow,
                filter = new { minVolume = minVol, minPct = minP, maxPct = maxP, windowSize = window, direction = dir, excludeCount = blacklist.Count, includeCount = whitelist?.Count ?? 0, maxSymbols = take },
                stages = new
                {
                    totalUsdtPairs = usdtSymbols.Count,
                    afterVolume = passVolume.Count,
                    afterPctRange = passPct.Count,
                    afterBlacklist = passBlacklist.Count,
                    afterWhitelist = passWhitelist.Count,
                },
                count = matches.Count,
                results = matches,
                nearMissPct,
            });
        }).RequireRateLimiting("scanner");

        grp.MapGet("/symbols", async (QuantFlowBotsDbContext db, CancellationToken ct) =>
        {
            var list = await db.Symbols
                .Where(s => s.IsActive)
                .OrderBy(s => s.Code)
                .Select(s => new { s.Id, s.Code, s.BaseAsset, s.QuoteAsset })
                .ToListAsync(ct);
            return Results.Ok(list);
        });

        grp.MapGet("/candles", async (
            string symbol,
            string interval,
            int? limit,
            IExchangeClient exchange,
            CancellationToken ct) =>
        {
            if (!Enum.TryParse<CandleInterval>(interval, true, out var iv))
                return Results.BadRequest(new { error = $"Unknown interval: {interval}" });
            var candles = await exchange.GetCandlesAsync(symbol, iv, null, null, limit ?? 200, ct);
            return Results.Ok(candles);
        });

        // VWAP (rational) vs MA (emotion) cross scanner. Scans top-100 USDT pairs.
        // Heavy on klines weight — protected by BinanceGate + a 60s shared scan cache.
        grp.MapGet("/vwap-cross-scan", async (
            string? anchor,
            int? maPeriod,
            string? interval,
            decimal? vwapFlatThresholdPct,
            string? direction,
            TickerSnapshotCache tickerCache,
            BinanceRestClient binance,
            VwapCrossScanCache cache,
            CancellationToken ct) =>
        {
            var anchorVal = (anchor ?? "daily").ToLowerInvariant();
            if (anchorVal is not ("daily" or "weekly" or "monthly")) return Results.BadRequest(new { error = "anchor must be daily | weekly | monthly" });
            var maP = Math.Clamp(maPeriod ?? 20, 5, 200);
            var ivStr = (interval ?? "1h").ToLowerInvariant();
            var ivEnumName = ivStr switch
            {
                "15m" => "FifteenMinutes", "30m" => "ThirtyMinutes",
                "1h" => "OneHour", "2h" => "TwoHours", "4h" => "FourHours",
                _ => "OneHour",
            };
            if (!Enum.TryParse<CandleInterval>(ivEnumName, true, out var iv)) iv = CandleInterval.OneHour;
            var flat = Math.Max(0.001m, vwapFlatThresholdPct ?? 0.05m);
            var dir = (direction ?? "both").ToLowerInvariant();
            if (dir is not ("buy" or "sell" or "both")) dir = "both";

            var key = $"{anchorVal}:{maP}:{ivStr}:{flat}:{dir}";
            var results = await cache.GetOrComputeAsync(key, async () =>
            {
                var tickers = await tickerCache.GetAsync(ct);
                var top = tickers
                    .Where(t => t.Symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase))
                    .Where(t => t.Symbol.All(c => c < 128 && (char.IsLetterOrDigit(c) || c == '_')))  // skip non-ASCII garbage tickers
                    .Where(t => Math.Abs(t.LastPrice - 1m) > 0.03m)   // skip stablecoin pegs
                    .OrderByDescending(t => t.QuoteVolume)
                    .Take(100)
                    .ToList();

                // Need history covering current anchor period + MA period + 2 bars for slope.
                var klineLimit = anchorVal switch { "weekly" => 200, "monthly" => 400, _ => 60 };
                klineLimit = Math.Max(klineLimit, maP + 5);

                var evals = new System.Collections.Concurrent.ConcurrentBag<VwapCrossEval>();
                await Parallel.ForEachAsync(top, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (ticker, c) =>
                {
                    try
                    {
                        var candles = await binance.GetCandlesAsync(ticker.Symbol, iv, null, null, klineLimit, c);
                        if (candles.Count < Math.Max(maP + 3, 10)) return;
                        var ev = EvaluateVwapCross(ticker.Symbol, candles, maP, anchorVal, flat, dir);
                        if (ev is not null) evals.Add(ev);
                    }
                    catch { /* one symbol failure must not poison the whole scan */ }
                });

                static object ToPayload(VwapCrossEval e) => new
                {
                    symbol = e.Symbol, side = e.Side, price = e.Price, ma = e.Ma, vwap = e.Vwap,
                    vwapSlopePct = e.VwapSlopePct, maDistanceFromVwapPct = e.MaDistanceFromVwapPct,
                    vwapAboveMa = e.VwapAboveMa, crossedAt = e.CrossedAt,
                    score = e.Score,
                    conditions = new { vwapFlat = e.VwapFlat, maReversed = e.MaReversed, crossed = e.Crossed },
                    missing = e.Score == 3
                        ? Array.Empty<string>()
                        : new[] { e.VwapFlat ? null : "vwap_flat", e.MaReversed ? null : "ma_reversal", e.Crossed ? null : "close_cross" }
                            .Where(x => x is not null).ToArray(),
                };

                var hits = evals.Where(e => e.Score == 3).OrderByDescending(e => e.VwapAboveMa).Select(ToPayload).ToList();
                var near = evals.Where(e => e.Score == 2).OrderByDescending(e => e.VwapAboveMa).Take(30).Select(ToPayload).ToList();

                return new
                {
                    updatedAt = DateTimeOffset.UtcNow,
                    filter = new { anchor = anchorVal, maPeriod = maP, interval = ivStr, vwapFlatThresholdPct = flat, direction = dir },
                    scanned = top.Count,
                    count = hits.Count,
                    nearCount = near.Count,
                    results = hits,
                    nearMatches = near,
                };
            }, ct);

            return Results.Ok(results);
        });

        // On-demand fresh depth snapshot for the wall-detail modal. Goes through BinanceGate
        // automatically so we don't burst Binance when the user spam-clicks walls.
        grp.MapGet("/depth", async (
            string symbol,
            int? limit,
            BinanceRestClient binance,
            CancellationToken ct) =>
        {
            var snap = await binance.GetDepthAsync(symbol.ToUpperInvariant(), Math.Clamp(limit ?? 50, 5, 100), ct);
            if (snap is null) return Results.NotFound(new { error = "depth_unavailable", symbol });
            return Results.Ok(new
            {
                symbol = snap.Symbol,
                at = snap.At,
                bids = snap.Bids.Select(l => new { price = l.Price, qty = l.Qty, notional = l.Price * l.Qty }),
                asks = snap.Asks.Select(l => new { price = l.Price, qty = l.Qty, notional = l.Price * l.Qty }),
            });
        });

        return app;
    }

    private static string? NormalizeWindowSize(string? raw)
    {
        var value = string.IsNullOrWhiteSpace(raw) ? "1h" : raw.Trim().ToLowerInvariant();
        if (value == "24h") value = "1d";
        if (value.EndsWith('m') && int.TryParse(value[..^1], out var minutes) && minutes is >= 1 and <= 59)
            return $"{minutes}m";
        if (value.EndsWith('h') && int.TryParse(value[..^1], out var hours) && hours is >= 1 and <= 23)
            return $"{hours}h";
        if (value.EndsWith('d') && int.TryParse(value[..^1], out var days) && days is >= 1 and <= 7)
            return $"{days}d";
        return null;
    }

    // Stable / wrapped-USD base assets to exclude from "Top Gainer / Top Volume" boards —
    // pairs like USDC/USDT have huge volume but are not actionable as trades.
    private static readonly HashSet<string> StableBaseAssets = new(StringComparer.OrdinalIgnoreCase)
    {
        "USDC", "FDUSD", "TUSD", "BUSD", "USDP", "USDD", "DAI", "SUSD", "USD1",
        "EUR", "EURI", "AEUR", "EURT", "PYUSD", "RLUSD", "UUSD", "USTC",
    };

    private static bool IsExcludedStablePair(string symbol, IReadOnlyList<string> quoteAssets, ISet<string> excludedBaseAssets)
    {
        foreach (var quote in quoteAssets.OrderByDescending(q => q.Length))
        {
            if (!symbol.EndsWith(quote, StringComparison.OrdinalIgnoreCase)) continue;
            var baseAsset = symbol[..^quote.Length];
            return excludedBaseAssets.Contains(baseAsset);
        }
        return false;
    }

    public sealed record VwapCrossEval(
        string Symbol, string Side, decimal Price, decimal Ma, decimal Vwap,
        decimal VwapSlopePct, decimal MaDistanceFromVwapPct, bool VwapAboveMa,
        DateTimeOffset CrossedAt, bool VwapFlat, bool MaReversed, bool Crossed, int Score);

    /// <summary>
    /// Evaluates the VWAP-flat + MA-reversal + close-cross rule on a candle series.
    /// Returns null only when there is not enough data; otherwise returns flags for each
    /// individual condition so the caller can split into full hits (3/3) vs near matches (2/3).
    /// </summary>
    private static VwapCrossEval? EvaluateVwapCross(string symbol, IReadOnlyList<CandleData> candles,
        int maPeriod, string anchor, decimal flatPct, string direction)
    {
        var n = candles.Count;
        if (n < maPeriod + 3) return null;

        var currClose = candles[n - 1].Close;
        var prevClose = candles[n - 2].Close;
        decimal? Sma(int end) {
            if (end - maPeriod + 1 < 0) return null;
            decimal s = 0m;
            for (var i = end - maPeriod + 1; i <= end; i++) s += candles[i].Close;
            return s / maPeriod;
        }
        var maCurr = Sma(n - 1); var maPrev = Sma(n - 2); var maPrev2 = Sma(n - 3);
        if (maCurr is null || maPrev is null || maPrev2 is null) return null;

        var slopePrev = maPrev.Value - maPrev2.Value;
        var slopeCurr = maCurr.Value - maPrev.Value;
        var reversedUp = slopePrev <= 0m && slopeCurr > 0m;
        var reversedDown = slopePrev >= 0m && slopeCurr < 0m;

        var anchorStart = anchor switch
        {
            "weekly" => candles[n - 1].OpenTime.AddDays(-(int)candles[n - 1].OpenTime.DayOfWeek).Date,
            "monthly" => new DateTime(candles[n - 1].OpenTime.Year, candles[n - 1].OpenTime.Month, 1, 0, 0, 0, DateTimeKind.Utc),
            _ => candles[n - 1].OpenTime.Date,
        };
        decimal? Vwap(int endIdx)
        {
            decimal pv = 0m, v = 0m;
            for (var i = endIdx; i >= 0; i--)
            {
                if (candles[i].OpenTime < anchorStart) break;
                var typical = (candles[i].High + candles[i].Low + candles[i].Close) / 3m;
                pv += typical * candles[i].Volume; v += candles[i].Volume;
            }
            return v <= 0m ? null : pv / v;
        }
        var vwapCurr = Vwap(n - 1); var vwapPrev = Vwap(n - 2);
        if (vwapCurr is null || vwapPrev is null || vwapCurr.Value <= 0m) return null;

        var vwapSlopePct = Math.Abs(vwapCurr.Value - vwapPrev.Value) / vwapCurr.Value * 100m;
        var vwapFlat = vwapSlopePct < flatPct;

        var crossedUp = prevClose <= maPrev.Value && currClose > maCurr.Value;
        var crossedDown = prevClose >= maPrev.Value && currClose < maCurr.Value;

        // Pick the side this candle is "leaning toward" by which of the directional conditions
        // it satisfies most. A symbol that has 0 directional conditions can't be a near match.
        var buyScore = (reversedUp ? 1 : 0) + (crossedUp ? 1 : 0);
        var sellScore = (reversedDown ? 1 : 0) + (crossedDown ? 1 : 0);
        string side;
        bool maReversed, crossed;
        if (buyScore > sellScore && (direction is "buy" or "both")) { side = "buy"; maReversed = reversedUp; crossed = crossedUp; }
        else if (sellScore > buyScore && (direction is "sell" or "both")) { side = "sell"; maReversed = reversedDown; crossed = crossedDown; }
        else if (buyScore > 0 && (direction is "buy" or "both")) { side = "buy"; maReversed = reversedUp; crossed = crossedUp; }
        else if (sellScore > 0 && (direction is "sell" or "both")) { side = "sell"; maReversed = reversedDown; crossed = crossedDown; }
        else return null;

        var score = (vwapFlat ? 1 : 0) + (maReversed ? 1 : 0) + (crossed ? 1 : 0);
        if (score == 0) return null;

        var maDistPct = (maCurr.Value - vwapCurr.Value) / vwapCurr.Value * 100m;
        return new VwapCrossEval(
            Symbol: symbol,
            Side: side,
            Price: currClose,
            Ma: Math.Round(maCurr.Value, 6),
            Vwap: Math.Round(vwapCurr.Value, 6),
            VwapSlopePct: Math.Round(vwapSlopePct, 4),
            MaDistanceFromVwapPct: Math.Round(maDistPct, 4),
            VwapAboveMa: vwapCurr.Value >= maCurr.Value,
            CrossedAt: candles[n - 1].CloseTime,
            VwapFlat: vwapFlat,
            MaReversed: maReversed,
            Crossed: crossed,
            Score: score);
    }
}

/// <summary>
/// 60-second TTL cache for the /vwap-cross-scan endpoint, keyed by the full filter combo.
/// Without this, every FE poll across users would re-scan top-100 × klines = ~200 weight burst.
/// </summary>
public sealed class VwapCrossScanCache
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset At, object Result, SemaphoreSlim Gate)> _entries = new();
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    public async Task<object> GetOrComputeAsync(string key, Func<Task<object>> compute, CancellationToken ct)
    {
        if (_entries.TryGetValue(key, out var hit) && DateTimeOffset.UtcNow - hit.At < Ttl) return hit.Result;

        var gate = _entries.GetOrAdd(key, _ => (DateTimeOffset.MinValue, new object(), new SemaphoreSlim(1, 1))).Gate;
        await gate.WaitAsync(ct);
        try
        {
            if (_entries.TryGetValue(key, out var fresh) && DateTimeOffset.UtcNow - fresh.At < Ttl) return fresh.Result;
            var result = await compute();
            _entries[key] = (DateTimeOffset.UtcNow, result, gate);
            return result;
        }
        finally { gate.Release(); }
    }
}

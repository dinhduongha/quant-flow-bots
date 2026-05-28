using QuantFlowBots.Application.Exchanges;
using QuantFlowBots.Application.Strategies;
using QuantFlowBots.Domain.Enums;

namespace QuantFlowBots.Infrastructure.Strategies;

/// <summary>
/// VWAP (rational) vs MA (emotion) cross strategy.
///
/// Entry rule (long):
///   1. VWAP slope ≈ 0 (|Δ/price| under threshold)            → rational sideways
///   2. MA slope flipped from non-positive to positive          → emotion reversal up
///   3. Close just crossed UP through MA (prev close ≤ MA[prev] AND curr close > MA[curr])
///
/// Quality signal (recorded in Metadata, not gating):
///   - `vwapAboveMa` true  → rational still leads emotion → sturdier breakout
///   - high  MA-vs-VWAP distance → emotion ran ahead → possible pump / fragile move
///
/// Anchored VWAP resets at the start of the chosen calendar period (day/week/month, UTC).
/// Daily/weekly anchor pair best with 1h candles; monthly with 2h.
/// </summary>
public sealed class VwapEmotionCrossStrategy : StrategyBase
{
    public const string KindCode = "vwap_emotion_cross";

    private int _maPeriod = 20;
    private string _vwapAnchor = "daily";
    private decimal _vwapFlatThresholdPct = 0.05m;
    private string _direction = "both";

    public override string Kind => KindCode;
    // Need MA period bars + at least a few extra for slope, and enough history
    // to cover the current VWAP anchor period.
    public override int WarmupBars => Math.Max(_maPeriod + 3, _vwapAnchor switch
    {
        "weekly" => 168,   // ~7d on 1h candles
        "monthly" => 360,  // ~30d on 2h candles
        _ => 24,
    });

    public override void Configure(IReadOnlyDictionary<string, object?> p)
    {
        _maPeriod = Math.Clamp(Int(p, "maPeriod", 20), 5, 200);
        _vwapAnchor = (Str(p, "vwapAnchor", "daily") ?? "daily").ToLowerInvariant();
        if (_vwapAnchor is not ("daily" or "weekly" or "monthly")) _vwapAnchor = "daily";
        _vwapFlatThresholdPct = Dec(p, "vwapFlatThresholdPct", 0.05m);
        _direction = (Str(p, "direction", "both") ?? "both").ToLowerInvariant();
        if (_direction is not ("buy" or "sell" or "both")) _direction = "both";
    }

    public override StrategyDecision? OnCandle(CandleData candle, IStrategyContext context)
    {
        var history = context.History;
        if (history.Count < WarmupBars) return null;

        var n = history.Count;
        var currClose = candle.Close;
        var prevClose = history[n - 2].Close;

        var maCurr = SmaClose(history, n - 1, _maPeriod);
        var maPrev = SmaClose(history, n - 2, _maPeriod);
        var maPrev2 = SmaClose(history, n - 3, _maPeriod);
        if (maCurr is null || maPrev is null || maPrev2 is null) return null;

        // MA slope reversal: previous slope ≤ 0, current slope > 0 → up reversal (and inverse).
        var slopePrev = maPrev.Value - maPrev2.Value;
        var slopeCurr = maCurr.Value - maPrev.Value;
        var reversedUp = slopePrev <= 0m && slopeCurr > 0m;
        var reversedDown = slopePrev >= 0m && slopeCurr < 0m;

        // Anchored VWAP for current and previous bar — to check flatness via Δ.
        var vwapCurr = AnchoredVwap(history, n - 1, _vwapAnchor);
        var vwapPrev = AnchoredVwap(history, n - 2, _vwapAnchor);
        if (vwapCurr is null || vwapPrev is null || vwapCurr.Value <= 0m) return null;
        var vwapSlopePct = Math.Abs(vwapCurr.Value - vwapPrev.Value) / vwapCurr.Value * 100m;
        var vwapFlat = vwapSlopePct < _vwapFlatThresholdPct;

        // Close-cross: previous close was on the opposite side of MA from current close.
        var crossedUp = prevClose <= maPrev.Value && currClose > maCurr.Value;
        var crossedDown = prevClose >= maPrev.Value && currClose < maCurr.Value;

        if (!vwapFlat) return null;
        if (context.OpenPositionQuantity is decimal q && q != 0m) return null;

        var quality = new Dictionary<string, object?>
        {
            ["vwap"] = Math.Round(vwapCurr.Value, 6),
            ["ma"] = Math.Round(maCurr.Value, 6),
            ["vwapSlopePct"] = Math.Round(vwapSlopePct, 4),
            ["maDistanceFromVwapPct"] = Math.Round((maCurr.Value - vwapCurr.Value) / vwapCurr.Value * 100m, 4),
            ["vwapAboveMa"] = vwapCurr.Value >= maCurr.Value,
            ["maPeriod"] = _maPeriod,
            ["vwapAnchor"] = _vwapAnchor,
        };

        if (reversedUp && crossedUp && (_direction is "buy" or "both"))
            return new StrategyDecision(SignalType.Entry, OrderSide.Buy, currClose, 1m,
                $"vwap flat ({vwapSlopePct:F3}%) + MA{_maPeriod} reversed up + close cross↑", quality);

        if (reversedDown && crossedDown && (_direction is "sell" or "both"))
            return new StrategyDecision(SignalType.Entry, OrderSide.Sell, currClose, 1m,
                $"vwap flat ({vwapSlopePct:F3}%) + MA{_maPeriod} reversed down + close cross↓", quality);

        return null;
    }

    private static decimal? SmaClose(IReadOnlyList<CandleData> history, int endIndex, int period)
    {
        if (endIndex - period + 1 < 0) return null;
        decimal sum = 0m;
        for (var i = endIndex - period + 1; i <= endIndex; i++) sum += history[i].Close;
        return sum / period;
    }

    private static decimal? AnchoredVwap(IReadOnlyList<CandleData> history, int endIndex, string anchor)
    {
        var endOpen = history[endIndex].OpenTime;
        var anchorStart = anchor switch
        {
            "weekly" => endOpen.AddDays(-(int)endOpen.DayOfWeek).Date,
            "monthly" => new DateTime(endOpen.Year, endOpen.Month, 1, 0, 0, 0, DateTimeKind.Utc),
            _ => endOpen.Date,
        };
        decimal pv = 0m, v = 0m;
        for (var i = endIndex; i >= 0; i--)
        {
            if (history[i].OpenTime < anchorStart) break;
            var typical = (history[i].High + history[i].Low + history[i].Close) / 3m;
            pv += typical * history[i].Volume;
            v += history[i].Volume;
        }
        return v <= 0m ? null : pv / v;
    }
}

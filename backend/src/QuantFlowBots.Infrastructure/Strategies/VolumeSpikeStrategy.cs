using QuantFlowBots.Application.Exchanges;
using QuantFlowBots.Application.Strategies;
using QuantFlowBots.Domain.Enums;

namespace QuantFlowBots.Infrastructure.Strategies;

/// <summary>
/// "Whale activity" strategy: fires when a candle's volume is N× the previous candle's,
/// indicating a sudden surge of buying or selling. Direction is inferred from price action
/// — bullish (close > open) for buy spike, bearish for sell spike.
///
/// Parameters (JSON):
///   multiplier   — required spike ratio vs the lookback baseline (default 5.0)
///   lookback     — N previous closed bars to average for baseline (default 20, clamp 5..50)
///   minVolume24h — drop signals on illiquid pairs; default 500_000 USDT cumulative over 24h
///   direction    — "buy" | "sell" | "both" (default "buy" — safer for spot/paper)
///
/// Bot timeframe is whatever candle interval the bot is wired to — pick 15m on the bot
/// itself, not in these params. That keeps the strategy interval-agnostic.
/// </summary>
public sealed class VolumeSpikeStrategy : StrategyBase
{
    public const string KindCode = "volume_spike";

    private decimal _multiplier = 5m;
    private int _lookback = 20;
    private decimal _minVolume24h = 500_000m;
    private string _direction = "buy";

    public override string Kind => KindCode;
    // Need enough history for the lookback baseline + 24h liquidity sum.
    public override int WarmupBars => Math.Max(100, _lookback + 2);

    public override void Configure(IReadOnlyDictionary<string, object?> p)
    {
        _multiplier = Dec(p, "multiplier", 5m);
        _lookback = Math.Clamp(Int(p, "lookback", 20), 5, 50);
        _minVolume24h = Dec(p, "minVolume24h", 500_000m);
        _direction = (Str(p, "direction", "buy") ?? "buy").ToLowerInvariant();
        if (_direction is not ("buy" or "sell" or "both")) _direction = "buy";
    }

    public override StrategyDecision? OnCandle(CandleData candle, IStrategyContext context)
    {
        var history = context.History;
        if (history.Count < WarmupBars) return null;

        // Average of last `_lookback` closed candles (excluding the current one — history[^1] == candle).
        decimal sum = 0m;
        for (var i = history.Count - 1 - _lookback; i < history.Count - 1; i++) sum += history[i].QuoteVolume;
        var avg = sum / _lookback;
        if (avg <= 0m) return null;

        var ratio = candle.QuoteVolume / avg;
        if (ratio < _multiplier) return null;

        // Liquidity floor based on cumulative quote volume over the last 24h of bars.
        decimal sum24h = 0m;
        var look = Math.Min(96, history.Count);
        for (var i = history.Count - look; i < history.Count; i++) sum24h += history[i].QuoteVolume;
        if (sum24h < _minVolume24h) return null;

        var isBuy = candle.Close > candle.Open;
        var isSell = candle.Close < candle.Open;
        var openQty = context.OpenPositionQuantity ?? 0m;
        var metadata = new Dictionary<string, object?>
        {
            ["ratio"] = Math.Round(ratio, 2),
            ["avgBaseline"] = avg,
            ["currVolume"] = candle.QuoteVolume,
            ["lookback"] = _lookback,
            ["sum24h"] = sum24h,
        };

        if (isBuy && (_direction is "buy" or "both") && openQty == 0)
            return new StrategyDecision(SignalType.Entry, OrderSide.Buy, candle.Close,
                Math.Min(1m, ratio / (_multiplier * 2m)),
                $"vol spike {ratio:F1}× (buy)", metadata);

        if (isSell && (_direction is "sell" or "both") && openQty == 0)
            return new StrategyDecision(SignalType.Entry, OrderSide.Sell, candle.Close,
                Math.Min(1m, ratio / (_multiplier * 2m)),
                $"vol spike {ratio:F1}× (sell)", metadata);

        return null;
    }
}

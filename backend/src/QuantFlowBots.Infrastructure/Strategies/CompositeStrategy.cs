using QuantFlowBots.Application.Exchanges;
using QuantFlowBots.Application.Strategies;
using QuantFlowBots.Domain.Enums;

namespace QuantFlowBots.Infrastructure.Strategies;

/// <summary>
/// Wraps N child strategies and emits an entry signal only when their decisions agree per the
/// configured combine logic. Used to express "I want SMA cross AND RSI oversold AND VWAP flat
/// before I open a position" without writing a new strategy.
///
/// Children are first-class strategies built via <see cref="IStrategyFactory"/>, so any kind
/// the factory knows about can be a child — including, theoretically, another composite (we
/// don't recurse intentionally but it would work).
/// </summary>
public sealed class CompositeStrategy(IStrategyFactory factory) : StrategyBase
{
    public const string KindCode = "composite";
    public override string Kind => KindCode;
    public override int WarmupBars => _children.Count == 0 ? 0 : _children.Max(c => c.WarmupBars);

    private readonly List<IStrategy> _children = [];
    private string _logic = "all";              // "all" | "any" | "quorum"
    private int _minMatch = 0;                  // quorum threshold; auto-derived if 0
    private bool _directionMustMatch = true;    // when true, mixed buy/sell signals → no signal

    public override void Configure(IReadOnlyDictionary<string, object?> p)
    {
        _logic = (Str(p, "logic", "all") ?? "all").ToLowerInvariant();
        if (_logic is not ("all" or "any" or "quorum"))
            throw new InvalidOperationException($"composite.logic must be 'all'|'any'|'quorum', got '{_logic}'");
        _minMatch = Int(p, "minMatch", 0);
        _directionMustMatch = p.TryGetValue("directionMustMatch", out var dmm) && dmm is bool b ? b : true;

        if (!p.TryGetValue("children", out var raw) || raw is not List<object?> rawList || rawList.Count == 0)
            throw new InvalidOperationException("composite needs a non-empty 'children' array");

        _children.Clear();
        foreach (var item in rawList)
        {
            if (item is not Dictionary<string, object?> child)
                throw new InvalidOperationException("composite.children[i] must be { kind, params }");
            var kind = child.TryGetValue("kind", out var k) ? k?.ToString() : null;
            if (string.IsNullOrWhiteSpace(kind))
                throw new InvalidOperationException("composite.children[i].kind is required");
            if (kind == KindCode)
                throw new InvalidOperationException("composite cannot nest composite (avoid recursion)");

            var paramsDict = child.TryGetValue("params", out var pv) && pv is Dictionary<string, object?> pd
                ? pd : new Dictionary<string, object?>();

            var strat = factory.Create(kind);
            strat.Configure(paramsDict);
            _children.Add(strat);
        }

        // Default quorum = simple majority — feels right for the "3 of 5 confirms" use case.
        if (_logic == "quorum" && _minMatch <= 0)
            _minMatch = (_children.Count / 2) + 1;
    }

    public override StrategyDecision? OnCandle(CandleData candle, IStrategyContext context)
    {
        // Run every child even if early ones say "no" — children are stateful (e.g. SMA windows)
        // and skipping them would desync their next-bar warmup math.
        var decisions = new List<StrategyDecision?>(_children.Count);
        foreach (var c in _children) decisions.Add(c.OnCandle(candle, context));

        // We only act on entry-side decisions; exits are bot-managed (SL/TP/auto-close).
        var entries = decisions.Where(d => d?.Type == SignalType.Entry).ToList();
        if (entries.Count == 0) return null;

        var buys = entries.Count(d => d!.Side == OrderSide.Buy);
        var sells = entries.Count(d => d!.Side == OrderSide.Sell);

        OrderSide consensus;
        int matchCount;
        if (_directionMustMatch)
        {
            // Strict: any disagreement = no signal. Safer for live trading.
            if (buys > 0 && sells == 0) { consensus = OrderSide.Buy; matchCount = buys; }
            else if (sells > 0 && buys == 0) { consensus = OrderSide.Sell; matchCount = sells; }
            else return null;
        }
        else
        {
            // Loose: pick the winning side, treat ties as no signal.
            if (buys > sells) { consensus = OrderSide.Buy; matchCount = buys; }
            else if (sells > buys) { consensus = OrderSide.Sell; matchCount = sells; }
            else return null;
        }

        var required = _logic switch
        {
            "all" => _children.Count,
            "any" => 1,
            "quorum" => _minMatch,
            _ => _children.Count
        };
        if (matchCount < required) return null;

        var contributing = entries.Where(d => d!.Side == consensus).ToList();
        var avgScore = contributing.Average(d => d!.Score);
        var reasonParts = contributing.Select(d => d!.Reason ?? "?");
        var logicTag = _logic == "quorum" ? $"quorum {matchCount}/{_children.Count}≥{_minMatch}"
                     : _logic == "any"    ? "any"
                                          : $"all {matchCount}/{_children.Count}";
        var reason = $"composite[{logicTag}]: " + string.Join(" + ", reasonParts);
        return new StrategyDecision(SignalType.Entry, consensus, candle.Close, avgScore, reason,
            Metadata: new Dictionary<string, object?>
            {
                ["compositeLogic"] = _logic,
                ["matchCount"] = matchCount,
                ["totalChildren"] = _children.Count,
            });
    }
}

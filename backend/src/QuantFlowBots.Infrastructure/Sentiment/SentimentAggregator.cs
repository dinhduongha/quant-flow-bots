using System.Collections.Concurrent;
using QuantFlowBots.Application.Sentiment;

namespace QuantFlowBots.Infrastructure.Sentiment;

/// <summary>
/// In-memory per-symbol exponentially-weighted sentiment. Each ingestion blends in with
/// alpha = 0.3 → newest events dominate but history smooths. Sample count is plain.
/// Thread-safe via per-symbol lock; bot runtimes / strategies read snapshots concurrently.
/// </summary>
public sealed class SentimentAggregator : ISentimentAggregator
{
    private const decimal Alpha = 0.3m;
    private readonly ConcurrentDictionary<string, State> _bySymbol = new(StringComparer.OrdinalIgnoreCase);

    public void Apply(ScoredSentiment evt)
    {
        var key = evt.SymbolCode.ToUpperInvariant();
        var state = _bySymbol.GetOrAdd(key, _ => new State());
        lock (state)
        {
            state.Count++;
            state.LatestScore = evt.Score;
            state.LatestAt = evt.At;
            var weighted = evt.Score * Math.Max(0.1m, evt.Magnitude);
            state.RollingScore = state.Count == 1
                ? weighted
                : (1m - Alpha) * state.RollingScore + Alpha * weighted;
            state.RollingMagnitude = state.Count == 1
                ? evt.Magnitude
                : (1m - Alpha) * state.RollingMagnitude + Alpha * evt.Magnitude;
        }
    }

    public SentimentSnapshot Get(string symbolCode)
    {
        var key = symbolCode.ToUpperInvariant();
        if (!_bySymbol.TryGetValue(key, out var s)) return new(key, 0m, 0m, 0, null, null);
        lock (s) return new(key, s.RollingScore, s.RollingMagnitude, s.Count, s.LatestScore, s.LatestAt);
    }

    public void Reset(string symbolCode)
        => _bySymbol.TryRemove(symbolCode.ToUpperInvariant(), out _);

    public void ResetAll() => _bySymbol.Clear();

    public IReadOnlyList<SentimentSnapshot> Top(int n, bool bullish = true)
    {
        var snaps = _bySymbol.Select(kv =>
        {
            lock (kv.Value)
                return new SentimentSnapshot(kv.Key, kv.Value.RollingScore, kv.Value.RollingMagnitude,
                    kv.Value.Count, kv.Value.LatestScore, kv.Value.LatestAt);
        });
        var ordered = bullish
            ? snaps.OrderByDescending(s => s.RollingScore)
            : snaps.OrderBy(s => s.RollingScore);
        return ordered.Take(n).ToList();
    }

    private sealed class State
    {
        public decimal RollingScore;
        public decimal RollingMagnitude;
        public int Count;
        public decimal? LatestScore;
        public DateTimeOffset? LatestAt;
    }
}

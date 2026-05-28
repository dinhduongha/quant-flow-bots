using System.Threading.Channels;

namespace QuantFlowBots.Application.Sentiment;

public sealed record SentimentInput(
    string SymbolCode,
    string Source,
    string Headline,
    string? Url,
    DateTimeOffset At,
    string? Tags = null);

public sealed record ScoredSentiment(
    string SymbolCode,
    string Source,
    string Headline,
    string? Url,
    decimal Score,
    decimal Magnitude,
    DateTimeOffset At,
    string? Tags);

public sealed record SentimentSnapshot(
    string SymbolCode,
    decimal RollingScore,
    decimal RollingMagnitude,
    int SampleCount,
    decimal? LatestScore,
    DateTimeOffset? LatestAt);

public interface ISentimentScorer
{
    ScoredSentiment Score(SentimentInput input);
}

public interface ISentimentBus
{
    ChannelReader<ScoredSentiment> Events { get; }
    ValueTask PublishAsync(ScoredSentiment evt, CancellationToken cancellationToken);
}

public interface ISentimentAggregator
{
    void Apply(ScoredSentiment evt);
    SentimentSnapshot Get(string symbolCode);
    IReadOnlyList<SentimentSnapshot> Top(int n, bool bullish = true);
    void Reset(string symbolCode);
    void ResetAll();
}

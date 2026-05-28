using QuantFlowBots.Application.Sentiment;
using QuantFlowBots.Infrastructure.Sentiment;
using Xunit;

namespace QuantFlowBots.Tests.Unit.Sentiment;

/// <summary>
/// Test pure function của <see cref="KeywordSentimentScorer"/>.
/// Mục đích: bảo vệ thuật toán phân loại khi sửa BullWords/BearWords (dễ regress).
/// Đây là test đầu tiên của solution — chạy bằng <c>dotnet test</c>.
/// </summary>
public sealed class KeywordSentimentScorerTests
{
    private static SentimentInput In(string headline) =>
        new("btcusdt", "test", headline, null, DateTimeOffset.UtcNow);

    [Fact]
    public void NeutralHeadline_ReturnsZeroScoreAndBaselineMagnitude()
    {
        var s = new KeywordSentimentScorer();
        var r = s.Score(In("Market remains quiet today"));
        Assert.Equal(0m, r.Score);
        Assert.Equal(0.1m, r.Magnitude); // baseline cho neutral
        Assert.Equal("BTCUSDT", r.SymbolCode); // upper-case normalize
    }

    [Fact]
    public void OnlyBullKeywords_ReturnsPositiveScore()
    {
        var s = new KeywordSentimentScorer();
        var r = s.Score(In("Bitcoin surge to ATH after ETF approval"));
        // 3 hits: surge, ath, etf, approve(d/al) — net positive
        Assert.True(r.Score > 0m, $"Expected positive, got {r.Score}");
        Assert.True(r.Magnitude > 0.1m);
    }

    [Fact]
    public void OnlyBearKeywords_ReturnsNegativeScore()
    {
        var s = new KeywordSentimentScorer();
        var r = s.Score(In("Major hack and exploit triggers crash and liquidations"));
        Assert.True(r.Score < 0m, $"Expected negative, got {r.Score}");
    }

    [Fact]
    public void MixedKeywords_ScoreReflectsNetSign()
    {
        var s = new KeywordSentimentScorer();
        var r = s.Score(In("Bull rally despite minor sell-off"));
        // 2 bull (bull, rally) vs 1 bear (sell-off) → net +1 / total 3 ≈ 0.333
        Assert.True(r.Score > 0m);
        Assert.True(r.Score < 1m);
    }

    [Fact]
    public void Magnitude_CapsAtOne()
    {
        var s = new KeywordSentimentScorer();
        // dồn nhiều keyword để total ≥ 4 (× 0.25 = 1.0 cap)
        var r = s.Score(In("surge rally moon bull bullish breakout pump"));
        Assert.Equal(1.0m, r.Magnitude);
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuantFlowBots.Api.Endpoints;

/// <summary>
/// 1-hour cache for alternative.me Fear &amp; Greed Index. Upstream refreshes daily at 00:00 UTC
/// so the long TTL is fine; it also protects against rate-limits and outages by serving the
/// last good payload during transient failures.
/// </summary>
public sealed class FearGreedCache
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private FearGreedSnapshot? _cached;
    private DateTimeOffset _cachedAt;
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(1);

    public async Task<FearGreedSnapshot?> GetOrFetchAsync(IHttpClientFactory httpFactory, CancellationToken ct)
    {
        if (_cached is not null && DateTimeOffset.UtcNow - _cachedAt < Ttl) return _cached;

        await _gate.WaitAsync(ct);
        try
        {
            if (_cached is not null && DateTimeOffset.UtcNow - _cachedAt < Ttl) return _cached;

            var http = httpFactory.CreateClient("alternative-me");
            try
            {
                var raw = await http.GetFromJsonAsync<AlternativeMeResponse>("fng/?limit=8", ct);
                if (raw?.Data is null || raw.Data.Count == 0) return _cached;

                // Data is returned newest-first; index 0 = today, 1 = yesterday, etc.
                var current = raw.Data[0];
                var history = raw.Data
                    .Reverse<AlternativeMeDataPoint>()
                    .Select(d => new FearGreedHistoryPoint(
                        Value: int.Parse(d.Value),
                        At: DateTimeOffset.FromUnixTimeSeconds(long.Parse(d.Timestamp))))
                    .ToList();

                _cached = new FearGreedSnapshot(
                    Value: int.Parse(current.Value),
                    Classification: current.ValueClassification,
                    UpdatedAt: DateTimeOffset.FromUnixTimeSeconds(long.Parse(current.Timestamp)),
                    NextUpdateInSeconds: int.TryParse(current.TimeUntilUpdate, out var s) ? s : null,
                    History: history);
                _cachedAt = DateTimeOffset.UtcNow;
                return _cached;
            }
            catch
            {
                // Serve stale cache if upstream is down — better than a flicker / blank panel.
                return _cached;
            }
        }
        finally { _gate.Release(); }
    }

    private sealed class AlternativeMeResponse
    {
        [JsonPropertyName("data")] public List<AlternativeMeDataPoint>? Data { get; set; }
    }

    private sealed class AlternativeMeDataPoint
    {
        [JsonPropertyName("value")] public string Value { get; set; } = "0";
        [JsonPropertyName("value_classification")] public string ValueClassification { get; set; } = "";
        [JsonPropertyName("timestamp")] public string Timestamp { get; set; } = "0";
        [JsonPropertyName("time_until_update")] public string? TimeUntilUpdate { get; set; }
    }
}

public sealed record FearGreedSnapshot(
    int Value,
    string Classification,
    DateTimeOffset UpdatedAt,
    int? NextUpdateInSeconds,
    IReadOnlyList<FearGreedHistoryPoint> History);

public sealed record FearGreedHistoryPoint(int Value, DateTimeOffset At);

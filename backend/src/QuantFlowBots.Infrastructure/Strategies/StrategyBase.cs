using System.Text.Json;
using QuantFlowBots.Application.Strategies;

namespace QuantFlowBots.Infrastructure.Strategies;

public abstract class StrategyBase : IStrategy
{
    public abstract string Kind { get; }
    public abstract int WarmupBars { get; }
    public abstract void Configure(IReadOnlyDictionary<string, object?> parameters);
    public abstract StrategyDecision? OnCandle(QuantFlowBots.Application.Exchanges.CandleData candle, IStrategyContext context);

    protected static int Int(IReadOnlyDictionary<string, object?> p, string key, int defaultValue)
        => p.TryGetValue(key, out var v) && v is not null && int.TryParse(v.ToString(), out var i) ? i : defaultValue;

    protected static decimal Dec(IReadOnlyDictionary<string, object?> p, string key, decimal defaultValue)
        => p.TryGetValue(key, out var v) && v is not null && decimal.TryParse(v.ToString(), out var d) ? d : defaultValue;

    protected static string? Str(IReadOnlyDictionary<string, object?> p, string key, string? defaultValue)
        => p.TryGetValue(key, out var v) && v is not null ? v.ToString() : defaultValue;

    public static IReadOnlyDictionary<string, object?> ParseJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}") return new Dictionary<string, object?>();
        // Accept `// inline` comments and trailing commas — lets the Strategies UI ship templates
        // annotated with explanations of each parameter without breaking the parser.
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        using var doc = JsonDocument.Parse(json, options);
        return (Dictionary<string, object?>)ParseElement(doc.RootElement)!;
    }

    // Deep parser so nested children (used by the composite strategy) come through with their
    // own Dictionary<string, object?> instead of flattening to a raw string.
    private static object? ParseElement(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Number => el.GetDecimal(),
        JsonValueKind.String => el.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Array => el.EnumerateArray().Select(ParseElement).ToList(),
        JsonValueKind.Object => el.EnumerateObject().ToDictionary(p => p.Name, p => ParseElement(p.Value)),
        _ => el.ToString(),
    };
}

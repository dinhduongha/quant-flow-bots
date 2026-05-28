namespace QuantFlowBots.Domain.Entities;

/// <summary>
/// Persistent record of a symbol that bots must NOT trade. Mirrors the in-memory state held
/// by <c>SymbolRiskGate</c> so flags survive Worker / API restarts. Unique per Symbol.
/// </summary>
public sealed class SymbolRiskFlag
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Symbol { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Source { get; set; } = "";
    public string? Url { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

using System.Collections.Concurrent;

namespace QuantFlowBots.Application.Risk;

/// <summary>
/// Singleton risk gate. In-memory hot path (IsBlocked / Snapshot are sync, O(1), called per
/// candle in BotRuntime) backed by a persistent SymbolRiskFlags table so flags survive Worker
/// and API restarts.
///
/// Mutations (BlockAsync / UnblockAsync) write the DB first, then update the in-memory dict
/// inside <see cref="ISymbolRiskGateStore"/>. The store is injected from Infrastructure to
/// keep this class free of EF references.
/// </summary>
public sealed class SymbolRiskGate(ISymbolRiskGateStore store)
{
    private readonly ConcurrentDictionary<string, RiskFlag> _flags =
        new(StringComparer.OrdinalIgnoreCase);

    public event Action<RiskFlag>? OnBlocked;

    // ---- hot path: lock-free reads ----
    public bool IsBlocked(string symbol) => _flags.ContainsKey(symbol);
    public RiskFlag? Get(string symbol) => _flags.TryGetValue(symbol, out var f) ? f : null;
    public IReadOnlyList<RiskFlag> Snapshot() => _flags.Values.OrderByDescending(f => f.At).ToList();

    /// <summary>Hydrates the in-memory dict from the persistent store. Call once at boot.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        var rows = await store.LoadAllAsync(ct);
        _flags.Clear();
        foreach (var f in rows) _flags[f.Symbol] = f;
    }

    /// <summary>
    /// Upserts a flag. Persists to DB first (so a restart preserves it) then mutates the dict.
    /// Fires <c>OnBlocked</c> only on the un→blocked transition for that symbol.
    /// </summary>
    public async Task BlockAsync(string symbol, string reason, string source, string? url, CancellationToken ct)
    {
        var flag = new RiskFlag(symbol.ToUpperInvariant(), reason, source, url, DateTimeOffset.UtcNow);
        await store.UpsertAsync(flag, ct);
        var added = _flags.TryAdd(flag.Symbol, flag);
        if (!added) _flags[flag.Symbol] = flag;
        if (added) OnBlocked?.Invoke(flag);
    }

    public async Task<bool> UnblockAsync(string symbol, CancellationToken ct)
    {
        var key = symbol.ToUpperInvariant();
        var removed = await store.DeleteAsync(key, ct);
        _flags.TryRemove(key, out _);
        return removed;
    }
}

public sealed record RiskFlag(string Symbol, string Reason, string Source, string? Url, DateTimeOffset At);

/// <summary>Storage backend for <see cref="SymbolRiskGate"/>. Implemented in Infrastructure.</summary>
public interface ISymbolRiskGateStore
{
    Task<IReadOnlyList<RiskFlag>> LoadAllAsync(CancellationToken ct);
    Task UpsertAsync(RiskFlag flag, CancellationToken ct);
    Task<bool> DeleteAsync(string symbol, CancellationToken ct);
}

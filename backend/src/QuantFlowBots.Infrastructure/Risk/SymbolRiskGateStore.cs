using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QuantFlowBots.Application.Risk;
using QuantFlowBots.Domain.Entities;
using QuantFlowBots.Infrastructure.Persistence;

namespace QuantFlowBots.Infrastructure.Risk;

/// <summary>EF-backed persistence for SymbolRiskGate. Uses a scope per call so we can be a
/// singleton consumed by the singleton gate without holding a DbContext.</summary>
public sealed class SymbolRiskGateStore(IServiceScopeFactory scopeFactory) : ISymbolRiskGateStore
{
    public async Task<IReadOnlyList<RiskFlag>> LoadAllAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QuantFlowBotsDbContext>();
        return await db.SymbolRiskFlags.AsNoTracking()
            .Select(r => new RiskFlag(r.Symbol, r.Reason, r.Source, r.Url, r.At))
            .ToListAsync(ct);
    }

    public async Task UpsertAsync(RiskFlag flag, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QuantFlowBotsDbContext>();
        var existing = await db.SymbolRiskFlags.FirstOrDefaultAsync(r => r.Symbol == flag.Symbol, ct);
        if (existing is null)
        {
            db.SymbolRiskFlags.Add(new SymbolRiskFlag
            {
                Symbol = flag.Symbol, Reason = flag.Reason, Source = flag.Source, Url = flag.Url,
                At = flag.At, CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            existing.Reason = flag.Reason;
            existing.Source = flag.Source;
            existing.Url = flag.Url;
            existing.At = flag.At;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> DeleteAsync(string symbol, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QuantFlowBotsDbContext>();
        var existing = await db.SymbolRiskFlags.FirstOrDefaultAsync(r => r.Symbol == symbol, ct);
        if (existing is null) return false;
        db.SymbolRiskFlags.Remove(existing);
        await db.SaveChangesAsync(ct);
        return true;
    }
}

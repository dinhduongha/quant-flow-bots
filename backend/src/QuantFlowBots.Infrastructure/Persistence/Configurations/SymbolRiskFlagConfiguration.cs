using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QuantFlowBots.Domain.Entities;

namespace QuantFlowBots.Infrastructure.Persistence.Configurations;

public sealed class SymbolRiskFlagConfiguration : IEntityTypeConfiguration<SymbolRiskFlag>
{
    public void Configure(EntityTypeBuilder<SymbolRiskFlag> e)
    {
        e.ToTable("symbol_risk_flags");
        e.HasKey(x => x.Id);
        e.Property(x => x.Symbol).HasMaxLength(20).IsRequired();
        e.Property(x => x.Reason).HasMaxLength(80).IsRequired();
        e.Property(x => x.Source).HasMaxLength(40).IsRequired();
        e.Property(x => x.Url).HasMaxLength(512);
        // One persistent flag per symbol. Re-block on the same symbol UPSERTs via SymbolRiskGate.
        e.HasIndex(x => x.Symbol).IsUnique();
    }
}

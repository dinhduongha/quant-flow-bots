using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using QuantFlowBots.Domain.Entities;

namespace QuantFlowBots.Infrastructure.Persistence;

public sealed class QuantFlowBotsDbContext(DbContextOptions<QuantFlowBotsDbContext> options)
    : IdentityDbContext<User, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<Exchange> Exchanges => Set<Exchange>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<Symbol> Symbols => Set<Symbol>();
    public DbSet<Candle> Candles => Set<Candle>();
    public DbSet<Strategy> Strategies => Set<Strategy>();
    public DbSet<Bot> Bots => Set<Bot>();
    public DbSet<BotRun> BotRuns => Set<BotRun>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<Position> Positions => Set<Position>();
    public DbSet<Signal> Signals => Set<Signal>();
    public DbSet<Backtest> Backtests => Set<Backtest>();
    public DbSet<RiskEvent> RiskEvents => Set<RiskEvent>();
    public DbSet<UserSettings> UserSettings => Set<UserSettings>();
    public DbSet<SentimentEvent> SentimentEvents => Set<SentimentEvent>();
    public DbSet<SymbolRiskFlag> SymbolRiskFlags => Set<SymbolRiskFlag>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.HasDefaultSchema("qfb");
        builder.Entity<User>().ToTable("users", "qfb");
        builder.Entity<IdentityRole<Guid>>().ToTable("roles", "qfb");
        builder.Entity<IdentityUserRole<Guid>>().ToTable("user_roles", "qfb");
        builder.Entity<IdentityUserClaim<Guid>>().ToTable("user_claims", "qfb");
        builder.Entity<IdentityUserLogin<Guid>>().ToTable("user_logins", "qfb");
        builder.Entity<IdentityRoleClaim<Guid>>().ToTable("role_claims", "qfb");
        builder.Entity<IdentityUserToken<Guid>>().ToTable("user_tokens", "qfb");

        builder.ApplyConfigurationsFromAssembly(typeof(QuantFlowBotsDbContext).Assembly);
    }
}

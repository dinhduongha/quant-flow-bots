using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using QuantFlowBots.Infrastructure;
using QuantFlowBots.Infrastructure.Exchanges;
using QuantFlowBots.Infrastructure.Persistence;
using QuantFlowBots.Infrastructure.Persistence.Seeding;
using QuantFlowBots.Worker;
using QuantFlowBots.Worker.Workers;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddUserSecrets<Program>(optional: true);

builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddSingleton(sp =>
{
    var cfg = builder.Configuration;
    var hubUrl = cfg["Realtime:ApiHubUrl"] ?? "http://localhost:5087/hubs/market";
    var signingKey = cfg["Jwt:SigningKey"] ?? throw new InvalidOperationException("Jwt:SigningKey is required.");
    var serviceToken = CreateServiceToken(cfg, signingKey);

    return new HubConnectionBuilder()
        .WithUrl(hubUrl, opt =>
        {
            opt.AccessTokenProvider = () => Task.FromResult<string?>(serviceToken);
        })
        .WithAutomaticReconnect()
        .Build();
});

builder.Services.AddSingleton<SignalRBroadcaster>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SignalRBroadcaster>());
builder.Services.AddHostedService<SymbolSeederWorker>();
builder.Services.AddHostedService<SymbolListingBackfillWorker>();
builder.Services.AddHostedService<MarketStreamWorker>();
builder.Services.AddHostedService<CandleIngestionWorker>();
builder.Services.AddHostedService<SignalScannerWorker>();
builder.Services.AddHostedService<BotExecutionWorker>();
builder.Services.AddHostedService<PositionMonitorWorker>();
builder.Services.AddHostedService<LivePositionReconcilerWorker>();
// WebSocket worker replaces the REST poller for near-realtime wall detection. The REST class
// stays in the codebase for fallback / debugging — re-enable by swapping the registration.
builder.Services.AddHostedService<OrderBookWallStreamWorker>();
// builder.Services.AddHostedService<OrderBookWallScannerWorker>();
builder.Services.AddHostedService<WhaleAlertWorker>();
builder.Services.AddHostedService<WallAlertWorker>();
builder.Services.AddHostedService<BinanceAnnouncementWorker>();
builder.Services.AddHostedService<SymbolStatusReconcilerWorker>();
builder.Services.AddHostedService<RiskGateEnforcerWorker>();

var host = builder.Build();

using (var scope = host.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<QuantFlowBotsDbContext>();
    await db.Database.MigrateAsync();
    await DbSeeder.EnsureTimescaleAsync(db, CancellationToken.None);
    await DbSeeder.SeedAsync(db, CancellationToken.None);
}

await host.RunAsync();

static string CreateServiceToken(IConfiguration cfg, string signingKey)
{
    var creds = new SigningCredentials(
        new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
        SecurityAlgorithms.HmacSha256);

    var token = new JwtSecurityToken(
        issuer: cfg["Jwt:Issuer"] ?? "quantflowbots",
        audience: cfg["Jwt:Audience"] ?? "quantflowbots.api",
        claims:
        [
            new Claim(JwtRegisteredClaimNames.Sub, cfg["Jwt:ServiceSubject"] ?? "service-worker"),
            new Claim("service", "worker")
        ],
        expires: DateTime.UtcNow.AddYears(1),
        signingCredentials: creds);

    return new JwtSecurityTokenHandler().WriteToken(token);
}

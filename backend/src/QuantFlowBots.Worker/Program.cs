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
// Worker là process duy nhất chạy các hosted service singleton-on-cluster
// (Telegram notifier, call-counter flush, risk gate bootstrap). API không gọi method này.
builder.Services.AddInfrastructureHostedServices();

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

// Migration là trách nhiệm CỦA API process (xem QuantFlowBots.Api/Program.cs).
// Worker chỉ chờ API hoàn tất migrate rồi mới chạy — tránh race trên __EFMigrationsLock
// nếu cả 2 process cùng start cold (deadlock được vì EF Core lock pessimistic).
// Vẫn gọi EnsureTimescaleAsync + SeedAsync ở Worker để Worker cũng tự khởi tạo được khi
// chạy standalone (test env), nhưng cả hai đều idempotent nên chạy 2 lần vô hại.
using (var scope = host.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<QuantFlowBotsDbContext>();
    var workerStartLogger = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()
        .CreateLogger("WorkerStartup");
    var waitDeadline = DateTimeOffset.UtcNow.AddMinutes(2);
    while (true)
    {
        try
        {
            var pending = await db.Database.GetPendingMigrationsAsync();
            if (!pending.Any()) break;
            workerStartLogger.LogInformation("Worker đang chờ API migrate xong — còn {Count} migration pending", pending.Count());
        }
        catch (Exception ex)
        {
            workerStartLogger.LogWarning(ex, "Worker chưa kết nối được DB, sẽ retry");
        }
        if (DateTimeOffset.UtcNow > waitDeadline)
            throw new InvalidOperationException("Worker chờ migration > 2 phút — API có chạy không?");
        await Task.Delay(TimeSpan.FromSeconds(3));
    }
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

using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using QuantFlowBots.Api.Auth;
using QuantFlowBots.Api.Endpoints;
using QuantFlowBots.Api.Hubs;
using QuantFlowBots.Infrastructure;
using QuantFlowBots.Infrastructure.Persistence;
using QuantFlowBots.Infrastructure.Persistence.Seeding;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddUserSecrets<Program>(optional: true);

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddScoped<JwtTokenService>();

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    p.AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials();

    if (builder.Environment.IsDevelopment())
    {
        p.SetIsOriginAllowed(origin =>
            Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)));
    }
    else
    {
        p.WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost:3000"]);
    }
}));

var jwt = builder.Configuration.GetSection("Jwt");
// Keep "sub" claim untouched; default handler maps sub -> ClaimTypes.NameIdentifier.
System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opt =>
    {
        opt.MapInboundClaims = false;
        opt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt["Issuer"],
            ValidAudience = jwt["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt["SigningKey"]!))
        };
        opt.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var token = ctx.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) && ctx.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                    ctx.Token = token;
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddSignalR().AddJsonProtocol(opt =>
{
    opt.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSingleton<QuantFlowBots.Api.Endpoints.VwapCrossScanCache>();
builder.Services.AddSingleton<QuantFlowBots.Api.Endpoints.FearGreedCache>();

// Per-IP throttle on Binance-burning endpoints. Each browser refresh / click on the FE filter
// triggers an outbound /api/v3/ticker rolling fetch — without this a few rapid clicks can
// burn enough weight to trip Binance's 1200/min limit and we get back to 418.
builder.Services.AddRateLimiter(opt =>
{
    opt.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    opt.AddPolicy("scanner", ctx => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
        {
            PermitLimit = 3,
            Window = TimeSpan.FromSeconds(5),
            QueueLimit = 0,
        }));
});

builder.Services.ConfigureHttpJsonOptions(opt =>
{
    opt.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    opt.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<QuantFlowBotsDbContext>();
    await db.Database.MigrateAsync();
    await DbSeeder.EnsureTimescaleAsync(db, CancellationToken.None);
    await DbSeeder.SeedAsync(db, CancellationToken.None);
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.UseSwagger();
app.UseSwaggerUI();

app.MapGet("/health", () => Results.Ok(new { service = "quant-flow-bots-api", status = "ok", utc = DateTimeOffset.UtcNow }));
app.MapAuth();
app.MapMarket();
app.MapStrategies();
app.MapBots();
app.MapBacktests();
app.MapSettings();
app.MapUserSettings();
app.MapSentiment();
app.MapHub<MarketHub>("/hubs/market");

app.Run();

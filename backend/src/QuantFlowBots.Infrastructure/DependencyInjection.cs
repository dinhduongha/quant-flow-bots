using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using QuantFlowBots.Application.Backtesting;
using QuantFlowBots.Application.Sentiment;
using QuantFlowBots.Application.Exchanges;
using QuantFlowBots.Application.Strategies;
using QuantFlowBots.Application.Streaming;
using QuantFlowBots.Application.Trading;
using QuantFlowBots.Infrastructure.Backtesting;
using QuantFlowBots.Domain.Entities;
using QuantFlowBots.Infrastructure.Exchanges.Binance;
using QuantFlowBots.Infrastructure.Notifications;
using QuantFlowBots.Infrastructure.Persistence;
using QuantFlowBots.Infrastructure.Security;
using QuantFlowBots.Infrastructure.Strategies;
using QuantFlowBots.Infrastructure.Streaming;
using QuantFlowBots.Infrastructure.Sentiment;
using QuantFlowBots.Infrastructure.Trading;
using QuantFlowBots.Infrastructure.Trading.BotKinds;
using QuantFlowBots.Infrastructure.Trading.LiveTrading;
using StackExchange.Redis;

namespace QuantFlowBots.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var connStr = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default is required.");

        services.AddDbContext<QuantFlowBotsDbContext>(opt =>
            opt.UseNpgsql(connStr, npg => npg.MigrationsHistoryTable("__ef_migrations_history", "qfb")));

        services.AddIdentityCore<User>(opt =>
            {
                opt.Password.RequireNonAlphanumeric = false;
                opt.Password.RequiredLength = 8;
                opt.User.RequireUniqueEmail = true;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<QuantFlowBotsDbContext>();

        services.Configure<BinanceOptions>(config.GetSection("Binance"));

        // Cross-process gate + per-process counter for all Binance REST traffic.
        // Attached to every typed client below via AddHttpMessageHandler — one chokepoint.
        services.AddSingleton<BinanceCallCounter>();
        services.AddHostedService<BinanceCallCounterFlushService>();
        services.AddSingleton<IBinanceGate, RedisBinanceGate>();
        services.AddSingleton<RateLimitManager>();
        services.AddTransient<BinanceGateHandler>();

        services.AddHttpClient<BinanceRestClient>((sp, client) =>
        {
            var opt = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<BinanceOptions>>().Value;
            client.BaseAddress = new Uri(opt.RestBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(15);
        }).AddHttpMessageHandler<BinanceGateHandler>();
        services.AddScoped<IExchangeClient>(sp => sp.GetRequiredService<BinanceRestClient>());

        services.AddSingleton<IMarketStreamClient, BinanceMarketStreamClient>();
        services.AddSingleton<ITickStreamClient, BinanceTickStreamClient>();

        services.AddSingleton<IMarketEventBus, InMemoryMarketEventBus>();
        services.AddSingleton<ISignalEventBus, InMemorySignalEventBus>();
        services.AddSingleton<InMemoryBotEventBus>();
        services.AddSingleton<IBotEventBus>(sp => sp.GetRequiredService<InMemoryBotEventBus>());
        services.AddSingleton<IOrderBookWallBus, InMemoryOrderBookWallBus>();
        services.AddSingleton<OrderBookWallCache>();
        services.Configure<OrderBookWallOptions>(config.GetSection("OrderBookWalls"));
        services.AddSingleton<ITickStreamBus, InMemoryTickStreamBus>();
        services.AddSingleton<TickerSnapshotCache>();
        services.AddSingleton<QuantFlowBots.Application.Risk.ISymbolRiskGateStore, QuantFlowBots.Infrastructure.Risk.SymbolRiskGateStore>();
        services.AddSingleton<QuantFlowBots.Application.Risk.SymbolRiskGate>();
        services.AddHostedService<QuantFlowBots.Infrastructure.Risk.RiskGateBootstrap>();
        services.AddSingleton<PositionMonitor>();

        services.AddSingleton<IApiKeyEncryption, AesApiKeyEncryption>();
        services.AddScoped<IExchangeCredentialStore, EfExchangeCredentialStore>();

        services.AddSingleton<StrategyFactory>();
        services.AddSingleton<IStrategyFactory>(sp => sp.GetRequiredService<StrategyFactory>());
        services.AddSingleton<IBotKindRuntime, SignalBotKindRuntime>();
        services.AddSingleton<IBotKindRuntime, DcaBotKindRuntime>();
        services.AddSingleton<IBotKindRuntime, GridBotKindRuntime>();
        services.AddSingleton<IBotKindRuntime, ScalpBotKindRuntime>();
        services.AddSingleton<BotKindRouter>();
        services.AddSingleton<BotRuntime>();
        services.AddScoped<IPaperOrderExecutor, PaperOrderExecutor>();
        services.AddScoped<IRiskEngine, RiskEngine>();
        services.AddHttpClient<BinanceFuturesRestClient>(c => c.Timeout = TimeSpan.FromSeconds(10))
            .AddHttpMessageHandler<BinanceGateHandler>();
        services.AddHttpClient<BinanceSpotSignedClient>(c => c.Timeout = TimeSpan.FromSeconds(10))
            .AddHttpMessageHandler<BinanceGateHandler>();
        services.AddSingleton<FuturesSymbolFiltersCache>();
        services.AddScoped<LiveFuturesExecutor>();
        services.AddScoped<LiveSpotExecutor>();
        services.AddScoped<ILiveTradingGate, LiveTradingGate>();
        services.AddScoped<ITradingDispatcher, TradingDispatcher>();
        services.AddSingleton<ISentimentScorer, KeywordSentimentScorer>();
        services.AddSingleton<ISentimentBus, InMemorySentimentBus>();
        services.AddSingleton<ISentimentAggregator, SentimentAggregator>();
        services.AddSingleton<ReconcileService>();
        services.AddHttpClient("telegram");
        services.AddHttpClient("alternative-me", c =>
        {
            c.BaseAddress = new Uri("https://api.alternative.me/");
            c.Timeout = TimeSpan.FromSeconds(8);
        });
        services.AddHostedService<TelegramNotifier>();
        services.AddScoped<IBacktestRunner, BacktestRunner>();

        var redis = config.GetConnectionString("Redis");
        if (!string.IsNullOrWhiteSpace(redis))
        {
            services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redis));
        }

        return services;
    }
}

using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QuantFlowBots.Application.Sentiment;
using QuantFlowBots.Application.Streaming;

namespace QuantFlowBots.Worker;

public sealed class SignalRBroadcaster(
    HubConnection hub,
    IMarketEventBus marketBus,
    ISignalEventBus signalBus,
    IBotEventBus botBus,
    ISentimentBus sentimentBus,
    IOrderBookWallBus wallBus,
    ILogger<SignalRBroadcaster> logger) : BackgroundService, IRealtimeBroadcaster
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ConnectWithRetryAsync(stoppingToken);

        var tickerTask = Pump(marketBus.SubscribeTickers().ReadAllAsync(stoppingToken), t => PushTickerAsync(t, stoppingToken));
        var klineTask = Pump(marketBus.SubscribeKlines().ReadAllAsync(stoppingToken), k => PushKlineAsync(k, stoppingToken));
        var signalTask = Pump(signalBus.Signals.ReadAllAsync(stoppingToken), s => PushSignalAsync(s, stoppingToken));
        var botTask = Pump(botBus.Events.ReadAllAsync(stoppingToken), b => PushBotEventAsync(b, stoppingToken));
        var sentimentTask = Pump(sentimentBus.Events.ReadAllAsync(stoppingToken), s => PushSentimentAsync(s, stoppingToken));
        var wallTask = Pump(wallBus.Walls.ReadAllAsync(stoppingToken), w => PushOrderBookWallAsync(w, stoppingToken));

        await Task.WhenAll(tickerTask, klineTask, signalTask, botTask, sentimentTask, wallTask);
    }

    public async Task PushOrderBookWallAsync(OrderBookWallEvent evt, CancellationToken cancellationToken)
    {
        if (hub.State == HubConnectionState.Connected)
            await hub.InvokeAsync("PublishOrderBookWall", evt, cancellationToken);
    }

    public async Task PushSentimentAsync(ScoredSentiment evt, CancellationToken cancellationToken)
    {
        if (hub.State == HubConnectionState.Connected)
            await hub.InvokeAsync("PublishSentiment", evt, cancellationToken);
    }

    private async Task ConnectWithRetryAsync(CancellationToken cancellationToken)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!cancellationToken.IsCancellationRequested && hub.State != HubConnectionState.Connected)
        {
            try { await hub.StartAsync(cancellationToken); logger.LogInformation("SignalR connected to API hub."); return; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "SignalR hub connect failed, retry in {S}s", backoff.TotalSeconds);
                try { await Task.Delay(backoff, cancellationToken); } catch { return; }
                backoff = TimeSpan.FromSeconds(Math.Min(30, backoff.TotalSeconds * 2));
            }
        }
    }

    private static async Task Pump<T>(IAsyncEnumerable<T> source, Func<T, Task> handler)
    {
        await foreach (var item in source)
        {
            try { await handler(item); } catch { /* swallow per-message errors */ }
        }
    }

    public async Task PushTickerAsync(TickerEvent evt, CancellationToken cancellationToken)
    {
        if (hub.State == HubConnectionState.Connected)
            await hub.InvokeAsync("PublishTicker", evt, cancellationToken);
    }

    public async Task PushKlineAsync(KlineEvent evt, CancellationToken cancellationToken)
    {
        if (hub.State == HubConnectionState.Connected)
            await hub.InvokeAsync("PublishKline", evt, cancellationToken);
    }

    public async Task PushSignalAsync(SignalEvent evt, CancellationToken cancellationToken)
    {
        if (hub.State == HubConnectionState.Connected)
            await hub.InvokeAsync("PublishSignal", evt, cancellationToken);
    }

    public async Task PushBotEventAsync(BotEvent evt, CancellationToken cancellationToken)
    {
        if (hub.State == HubConnectionState.Connected)
            await hub.InvokeAsync("PublishBotEvent", evt, cancellationToken);
    }
}

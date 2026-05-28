using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using QuantFlowBots.Application.Sentiment;
using QuantFlowBots.Application.Streaming;
using QuantFlowBots.Infrastructure.Trading;

namespace QuantFlowBots.Api.Hubs;

[Authorize]
public sealed class MarketHub(OrderBookWallCache wallCache) : Hub
{
    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "market");
        var userId = Context.UserIdentifier;
        if (!string.IsNullOrEmpty(userId))
            await Groups.AddToGroupAsync(Context.ConnectionId, $"user:{userId}");
        await base.OnConnectedAsync();
    }

    public Task JoinBotGroup(string botId) =>
        Groups.AddToGroupAsync(Context.ConnectionId, $"bot:{botId}");

    public Task LeaveBotGroup(string botId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, $"bot:{botId}");

    public Task PublishTicker(TickerEvent evt) =>
        Clients.Group("market").SendAsync("ticker", evt);

    public Task PublishKline(KlineEvent evt) =>
        Clients.Group("market").SendAsync("kline", evt);

    public Task PublishSignal(SignalEvent evt) =>
        Clients.Group("market").SendAsync("signal", evt);

    public Task PublishBotEvent(BotEvent evt) =>
        Clients.Group($"bot:{evt.BotId}").SendAsync("bot", evt);

    public Task PublishSentiment(ScoredSentiment evt) =>
        Clients.Group("market").SendAsync("sentiment", evt);

    public Task PublishOrderBookWall(OrderBookWallEvent evt)
    {
        wallCache.Upsert(evt); // keep API-side read cache in sync (Worker has its own copy)
        return Clients.Group("market").SendAsync("orderBookWall", evt);
    }
}

using QuantFlowBots.Application.Sentiment;

namespace QuantFlowBots.Application.Streaming;

public interface IRealtimeBroadcaster
{
    Task PushTickerAsync(TickerEvent evt, CancellationToken cancellationToken);
    Task PushKlineAsync(KlineEvent evt, CancellationToken cancellationToken);
    Task PushSignalAsync(SignalEvent evt, CancellationToken cancellationToken);
    Task PushBotEventAsync(BotEvent evt, CancellationToken cancellationToken);
    Task PushSentimentAsync(ScoredSentiment evt, CancellationToken cancellationToken);
    Task PushOrderBookWallAsync(OrderBookWallEvent evt, CancellationToken cancellationToken);
}

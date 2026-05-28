using System.Threading.Channels;

namespace QuantFlowBots.Application.Streaming;

public interface IMarketEventBus
{
    ValueTask PublishAsync(MarketEvent evt, CancellationToken cancellationToken);

    /// <summary>
    /// Creates an independent reader that receives a copy of every ticker event.
    /// A plain Channel has at-most-one delivery per item, so when several workers read the same
    /// channel they STEAL events from each other. Each consumer must call Subscribe* once (at
    /// startup) to get its own fan-out channel. Channels are bounded + drop-oldest, so a slow
    /// consumer drops its own backlog without affecting others or blocking the publisher.
    /// </summary>
    ChannelReader<TickerEvent> SubscribeTickers();

    /// <inheritdoc cref="SubscribeTickers"/>
    ChannelReader<KlineEvent> SubscribeKlines();
}

public interface ISignalEventBus
{
    ValueTask PublishAsync(SignalEvent evt, CancellationToken cancellationToken);
    ChannelReader<SignalEvent> Signals { get; }
}

public interface IBotEventBus
{
    ValueTask PublishAsync(BotEvent evt, CancellationToken cancellationToken);
    ChannelReader<BotEvent> Events { get; }
}

public interface IOrderBookWallBus
{
    ValueTask PublishAsync(OrderBookWallEvent evt, CancellationToken cancellationToken);
    ChannelReader<OrderBookWallEvent> Walls { get; }
    /// <summary>Fan-out hook for additional consumers (e.g. Telegram wall alerts). The Channel
    /// above is single-consumer (SignalR broadcaster); event lets others observe in parallel.</summary>
    event Action<OrderBookWallEvent>? OnWall;
}

public interface ITickStreamBus
{
    ValueTask PublishBookTickerAsync(BookTickerEvent evt, CancellationToken cancellationToken);
    ValueTask PublishAggTradeAsync(AggTradeEvent evt, CancellationToken cancellationToken);
    ChannelReader<BookTickerEvent> BookTickers { get; }
    ChannelReader<AggTradeEvent> AggTrades { get; }
}

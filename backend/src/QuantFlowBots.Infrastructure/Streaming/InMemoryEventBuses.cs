using System.Threading.Channels;
using QuantFlowBots.Application.Streaming;

namespace QuantFlowBots.Infrastructure.Streaming;

public sealed class InMemoryMarketEventBus : IMarketEventBus
{
    // One channel per subscriber (fan-out). Copy-on-write arrays so the hot publish path is
    // lock-free; subscriptions only happen at worker startup. TryWrite + DropOldest means the
    // WS pump never blocks on a slow consumer — each subscriber drops only its own backlog.
    private volatile Channel<TickerEvent>[] _tickerSubs = [];
    private volatile Channel<KlineEvent>[] _klineSubs = [];
    private readonly object _subLock = new();

    public ChannelReader<TickerEvent> SubscribeTickers()
    {
        var ch = Channel.CreateBounded<TickerEvent>(
            new BoundedChannelOptions(8192) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
        lock (_subLock) { _tickerSubs = [.. _tickerSubs, ch]; }
        return ch.Reader;
    }

    public ChannelReader<KlineEvent> SubscribeKlines()
    {
        var ch = Channel.CreateBounded<KlineEvent>(
            new BoundedChannelOptions(4096) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
        lock (_subLock) { _klineSubs = [.. _klineSubs, ch]; }
        return ch.Reader;
    }

    public ValueTask PublishAsync(MarketEvent evt, CancellationToken cancellationToken)
    {
        switch (evt)
        {
            case TickerEvent t:
                foreach (var ch in _tickerSubs) ch.Writer.TryWrite(t);
                break;
            case KlineEvent k:
                foreach (var ch in _klineSubs) ch.Writer.TryWrite(k);
                break;
        }
        return ValueTask.CompletedTask;
    }
}

public sealed class InMemorySignalEventBus : ISignalEventBus
{
    private readonly Channel<SignalEvent> _channel = Channel.CreateBounded<SignalEvent>(
        new BoundedChannelOptions(2048) { FullMode = BoundedChannelFullMode.DropOldest });

    public ChannelReader<SignalEvent> Signals => _channel.Reader;
    public ValueTask PublishAsync(SignalEvent evt, CancellationToken cancellationToken) => _channel.Writer.WriteAsync(evt, cancellationToken);
}

public sealed class InMemoryBotEventBus : IBotEventBus
{
    private readonly Channel<BotEvent> _channel = Channel.CreateBounded<BotEvent>(
        new BoundedChannelOptions(2048) { FullMode = BoundedChannelFullMode.DropOldest });

    /// <summary>
    /// Fan-out hook for additional subscribers (e.g. Telegram notifier).
    /// The primary channel still feeds the SignalR broadcaster; this event fires in parallel.
    /// Handlers should be best-effort and never throw — exceptions are swallowed below.
    /// </summary>
    public event Func<BotEvent, CancellationToken, Task>? OnEvent;

    public ChannelReader<BotEvent> Events => _channel.Reader;

    public async ValueTask PublishAsync(BotEvent evt, CancellationToken cancellationToken)
    {
        await _channel.Writer.WriteAsync(evt, cancellationToken);
        var handlers = OnEvent;
        if (handlers is null) return;
        foreach (Func<BotEvent, CancellationToken, Task> h in handlers.GetInvocationList())
        {
            try { _ = h.Invoke(evt, cancellationToken); } catch { /* fan-out is best-effort */ }
        }
    }
}

public sealed class InMemoryTickStreamBus : ITickStreamBus
{
    private readonly Channel<BookTickerEvent> _book = Channel.CreateBounded<BookTickerEvent>(
        new BoundedChannelOptions(16384) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = false, SingleWriter = false });
    private readonly Channel<AggTradeEvent> _trades = Channel.CreateBounded<AggTradeEvent>(
        new BoundedChannelOptions(16384) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = false, SingleWriter = false });

    public ChannelReader<BookTickerEvent> BookTickers => _book.Reader;
    public ChannelReader<AggTradeEvent> AggTrades => _trades.Reader;
    public ValueTask PublishBookTickerAsync(BookTickerEvent evt, CancellationToken cancellationToken) => _book.Writer.WriteAsync(evt, cancellationToken);
    public ValueTask PublishAggTradeAsync(AggTradeEvent evt, CancellationToken cancellationToken) => _trades.Writer.WriteAsync(evt, cancellationToken);
}

public sealed class InMemoryOrderBookWallBus : IOrderBookWallBus
{
    private readonly Channel<OrderBookWallEvent> _channel = Channel.CreateBounded<OrderBookWallEvent>(
        new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.DropOldest });

    public ChannelReader<OrderBookWallEvent> Walls => _channel.Reader;
    public event Action<OrderBookWallEvent>? OnWall;

    public async ValueTask PublishAsync(OrderBookWallEvent evt, CancellationToken cancellationToken)
    {
        await _channel.Writer.WriteAsync(evt, cancellationToken);
        // Fire event AFTER channel write so a slow subscriber can't block the SignalR pump.
        try { OnWall?.Invoke(evt); } catch { /* subscriber errors must not break publish */ }
    }
}

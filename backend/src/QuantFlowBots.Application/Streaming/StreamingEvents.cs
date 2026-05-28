using QuantFlowBots.Application.Exchanges;

namespace QuantFlowBots.Application.Streaming;

public abstract record MarketEvent(string Exchange, string Symbol, DateTimeOffset At);

public sealed record TickerEvent(string Exchange, string Symbol, decimal Price, decimal PriceChangePercent, decimal QuoteVolume, DateTimeOffset At)
    : MarketEvent(Exchange, Symbol, At);

public sealed record KlineEvent(string Exchange, string Symbol, CandleData Candle, DateTimeOffset At)
    : MarketEvent(Exchange, Symbol, At);

public sealed record SignalEvent(Guid SignalId, Guid StrategyId, string Symbol, string Type, string? Side, decimal Price, decimal Score, DateTimeOffset At);

public sealed record BotEvent(Guid BotId, string Kind, string Message, DateTimeOffset At);

public sealed record BookTickerEvent(
    string Exchange,
    string Symbol,
    decimal BestBid,
    decimal BestBidQty,
    decimal BestAsk,
    decimal BestAskQty,
    DateTimeOffset At) : MarketEvent(Exchange, Symbol, At);

public sealed record AggTradeEvent(
    string Exchange,
    string Symbol,
    decimal Price,
    decimal Quantity,
    bool IsBuyerMaker,
    DateTimeOffset TradeTime,
    DateTimeOffset At) : MarketEvent(Exchange, Symbol, At);

public sealed record PositionClosedAutoEvent(
    Guid PositionId,
    Guid BotId,
    string Symbol,
    string Reason,
    decimal TriggerPrice,
    decimal RealizedPnl,
    DateTimeOffset At);

public sealed record OrderBookWallEvent(
    string Symbol,
    string Side,
    decimal Price,
    decimal Quantity,
    decimal QuoteNotional,
    decimal MidPrice,
    decimal DistanceFromMidPercent,
    decimal Multiplier,
    DateTimeOffset At);


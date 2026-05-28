namespace QuantFlowBots.Domain.Enums;

public enum TradingMode
{
    Paper = 0,
    Live = 1
}

public enum BotState
{
    Stopped = 0,
    Running = 1,
    Paused = 2,
    Errored = 3
}

public enum OrderSide
{
    Buy = 0,
    Sell = 1
}

public enum OrderType
{
    Market = 0,
    Limit = 1,
    StopLoss = 2,
    TakeProfit = 3
}

public enum OrderStatus
{
    New = 0,
    PartiallyFilled = 1,
    Filled = 2,
    Canceled = 3,
    Rejected = 4,
    Expired = 5
}

public enum PositionSide
{
    Long = 0,
    Short = 1
}

public enum PositionStatus
{
    Open = 0,
    Closed = 1
}

public enum SignalType
{
    Entry = 0,
    Exit = 1,
    Warning = 2
}

public enum CandleInterval
{
    OneMinute = 60,
    FiveMinutes = 300,
    FifteenMinutes = 900,
    ThirtyMinutes = 1800,
    OneHour = 3600,
    TwoHours = 7200,
    FourHours = 14400,
    OneDay = 86400
}

public enum StopLossKind
{
    FixedPercent = 0,
    Atr = 1
}

public enum BotRunMode
{
    Off = 0,
    ScanOnly = 1,
    PaperTrading = 2,
    LiveTrading = 3
}

public enum BotKind
{
    Signal = 0,
    Dca = 1,
    Grid = 2,
    Scalp = 3
}

public enum BacktestStatus
{
    Queued = 0,
    Running = 1,
    Completed = 2,
    Failed = 3
}

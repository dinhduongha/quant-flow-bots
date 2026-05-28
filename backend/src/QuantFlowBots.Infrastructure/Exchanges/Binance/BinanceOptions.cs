namespace QuantFlowBots.Infrastructure.Exchanges.Binance;

public sealed class BinanceOptions
{
    public string RestBaseUrl { get; set; } = "https://api.binance.com";
    public string WebSocketBaseUrl { get; set; } = "wss://stream.binance.com:9443";
    public string[] WatchSymbols { get; set; } = ["BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT"];

    /// <summary>Spot REST IP weight budget per rolling minute (Binance default = 6000).</summary>
    public int WeightLimitPerMinute { get; set; } = 6000;

    /// <summary>At/above this % of the limit we start throttling (delaying) every request.</summary>
    public int WeightSlowDownPercent { get; set; } = 70;

    /// <summary>At/above this % we reject NON-critical (market-data) requests; trading still flows.</summary>
    public int WeightCriticalOnlyPercent { get; set; } = 85;
}

using QuantFlowBots.Domain.Enums;

namespace QuantFlowBots.Application.Streaming;

public interface IMarketStreamClient
{
    string ExchangeCode { get; }

    /// <summary>
    /// Declaratively sets the full desired subscription (replaces any previous one). Safe to call
    /// repeatedly: if the resulting stream list is unchanged it's a no-op; if it changed and
    /// <see cref="RunAsync"/> is active, the underlying WebSocket reconnects with the new streams.
    /// This lets the subscription track a moving target (e.g. symbols of currently-running bots)
    /// without resubscribing to the whole market.
    /// </summary>
    void SetSubscriptions(IEnumerable<string> tickerSymbols, IEnumerable<string> klineSymbols, CandleInterval klineInterval);

    Task RunAsync(CancellationToken cancellationToken);
}

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QuantFlowBots.Application.Streaming;
using QuantFlowBots.Infrastructure.Trading;

namespace QuantFlowBots.Worker.Workers;

public sealed class SignalScannerWorker(
    IMarketEventBus marketBus,
    BotRuntime runtime,
    ILogger<SignalScannerWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("SignalScannerWorker started, loading running bots...");
        await runtime.LoadRunningAsync(stoppingToken);

        await foreach (var evt in marketBus.SubscribeKlines().ReadAllAsync(stoppingToken))
        {
            if (!evt.Candle.IsClosed) continue;
            await runtime.OnCandleClosedAsync(evt, stoppingToken);
        }
    }
}

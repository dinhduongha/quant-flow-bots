using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QuantFlowBots.Application.Risk;

namespace QuantFlowBots.Infrastructure.Risk;

/// <summary>
/// Hydrates SymbolRiskGate from the persistent store before any worker / endpoint reads from it.
/// Registered as a hosted service in both API and Worker so each process has the up-to-date set
/// of flags even after a cold start.
/// </summary>
public sealed class RiskGateBootstrap(SymbolRiskGate gate, ILogger<RiskGateBootstrap> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await gate.LoadAsync(cancellationToken);
            logger.LogInformation("SymbolRiskGate hydrated with {N} flag(s) from DB", gate.Snapshot().Count);
        }
        catch (Exception ex)
        {
            // Don't crash the process if the load fails on boot; workers will re-block within their
            // poll interval and persist again. Surface as warning so it doesn't get lost.
            logger.LogWarning(ex, "SymbolRiskGate failed to load from DB on startup");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

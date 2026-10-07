using DurianRoute.Api.Hubs;
using DurianRoute.Shared;
using Microsoft.AspNetCore.SignalR;

namespace DurianRoute.Api.Traffic;

/// <summary>Refreshes live choke point conditions and pushes them to every dashboard.</summary>
public class TrafficSimulator(
    LiveTrafficState state,
    IHubContext<TelemetryHub> hub,
    ILogger<TrafficSimulator> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var rng = new Random();
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                var statuses = state.Update(DateTime.UtcNow, rng);
                await hub.Clients.All.SendAsync(HubEvents.ChokePointStatus, statuses, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Traffic simulation tick failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

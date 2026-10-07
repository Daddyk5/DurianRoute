using DurianRoute.Api.Data;
using DurianRoute.Api.Hubs;
using DurianRoute.Api.Traffic;
using DurianRoute.Shared;
using Microsoft.AspNetCore.SignalR;

namespace DurianRoute.Api.Simulation;

/// <summary>
/// Moves the simulated fleet once per second and streams positions to the SignalR groups of
/// each route. Deviation alerts are persisted and broadcast to all dispatchers.
/// </summary>
public class BusSimulator(
    FleetState fleet,
    LiveTrafficState traffic,
    IHubContext<TelemetryHub> hub,
    IServiceScopeFactory scopes,
    IConfiguration config,
    ILogger<BusSimulator> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var speed = Math.Clamp(config.GetValue("Simulation:SpeedMultiplier", 1.0), 0.1, 60);
        var rng = new Random();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var now = DateTime.UtcNow;
                var (positions, events) = fleet.Tick(now, speed, traffic, rng);

                foreach (var group in positions.GroupBy(p => p.RouteId))
                    await hub.Clients.Group(TelemetryHub.RouteGroup(group.Key))
                        .SendAsync(HubEvents.BusPositions, group.ToList(), stoppingToken);

                if (events.Count > 0)
                    await PublishDeviationsAsync(events, now, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Bus simulation tick failed");
            }
        }
    }

    private async Task PublishDeviationsAsync(List<DeviationEvent> events, DateTime now, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DurianDbContext>();

        var rows = events.Select(e => new ScheduleDeviation
        {
            BusId = e.BusId,
            DeviationMinutes = e.DeviationMinutes,
            NearStop = e.NearStop,
            Message = e.Message,
            TimestampUtc = now
        }).ToList();
        db.ScheduleDeviations.AddRange(rows);
        await db.SaveChangesAsync(ct);

        for (var i = 0; i < rows.Count; i++)
        {
            var e = events[i];
            var dto = new DeviationAlertDto(rows[i].Id, e.BusId, e.PlateNumber, e.RouteCode, e.DeviationMinutes, e.NearStop, e.Message, now);
            await hub.Clients.All.SendAsync(HubEvents.DeviationAlert, dto, ct);
        }
    }
}

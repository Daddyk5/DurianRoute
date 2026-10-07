using DurianRoute.Api.Simulation;
using DurianRoute.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace DurianRoute.Api.Hubs;

/// <summary>
/// Bi-directional telemetry channel. Server → client: bus positions (per route group), choke point
/// status, deviation alerts and lane changes. Client → server: route subscriptions and dispatcher
/// commands (hold / release a bus).
/// </summary>
[Authorize]
public class TelemetryHub(FleetState fleet, ILogger<TelemetryHub> logger) : Hub
{
    public static string RouteGroup(int routeId) => $"route:{routeId}";

    public override async Task OnConnectedAsync()
    {
        // Dashboards see every route until they narrow the view.
        foreach (var routeId in fleet.RouteIds)
            await Groups.AddToGroupAsync(Context.ConnectionId, RouteGroup(routeId));
        await base.OnConnectedAsync();
    }

    public Task SubscribeRoute(int routeId) =>
        Groups.AddToGroupAsync(Context.ConnectionId, RouteGroup(routeId));

    public Task UnsubscribeRoute(int routeId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, RouteGroup(routeId));

    [Authorize(Roles = Roles.AdminOrDispatcher)]
    public CommandResultDto HoldBus(int busId, int minutes)
    {
        var ok = fleet.Hold(busId, minutes, DateTime.UtcNow);
        logger.LogInformation("{User} held bus {BusId} for {Minutes} min", Context.User?.Identity?.Name, busId, minutes);
        return ok
            ? new CommandResultDto(true, $"Bus held for {minutes} min")
            : new CommandResultDto(false, "Unknown bus");
    }

    [Authorize(Roles = Roles.AdminOrDispatcher)]
    public CommandResultDto ReleaseBus(int busId)
    {
        var ok = fleet.Release(busId);
        logger.LogInformation("{User} released bus {BusId}", Context.User?.Identity?.Name, busId);
        return ok ? new CommandResultDto(true, "Bus released") : new CommandResultDto(false, "Unknown bus");
    }
}

using DurianRoute.Api.Data;
using DurianRoute.Api.Traffic;
using DurianRoute.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DurianRoute.Api.Controllers;

/// <summary>Static network data (routes, stops, choke points) plus live choke point status.</summary>
[ApiController]
[Authorize]
[Route("api")]
public class NetworkController(DurianDbContext db, LiveTrafficState live) : ControllerBase
{
    [HttpGet("routes")]
    public async Task<List<RouteDto>> Routes(CancellationToken ct)
    {
        var routes = await db.Routes.AsNoTracking().Include(r => r.Stops).OrderBy(r => r.Code).ToListAsync(ct);
        return routes.Select(r =>
        {
            var stops = r.Stops.OrderBy(s => s.Sequence).ToList();
            return new RouteDto(r.Id, r.Code, r.Name, r.Color,
                stops.Select(s => new LatLng(s.Lat, s.Lng)).ToList(),
                stops.Select(s => new StopDto(s.Id, s.Name, s.Lat, s.Lng, s.Sequence)).ToList());
        }).ToList();
    }

    [HttpGet("chokepoints")]
    public List<ChokePointDto> ChokePoints() =>
        live.ChokePoints
            .OrderBy(c => c.Id)
            .Select(c => new ChokePointDto(c.Id, c.Name, c.Lat, c.Lng, c.LanesPerDirection, c.HasReversibleLane, c.ActiveLaneState))
            .ToList();

    [HttpGet("chokepoints/status")]
    public List<ChokePointStatusDto> Status() => live.Snapshot();

    [HttpGet("deviations")]
    public async Task<List<DeviationAlertDto>> Deviations([FromQuery] int take = 100, CancellationToken ct = default)
    {
        var rows = await db.ScheduleDeviations.AsNoTracking()
            .Include(d => d.Bus).ThenInclude(b => b!.Route)
            .OrderByDescending(d => d.TimestampUtc)
            .Take(Math.Clamp(take, 1, 500))
            .ToListAsync(ct);
        return rows.Select(d => new DeviationAlertDto(d.Id, d.BusId, d.Bus!.PlateNumber, d.Bus.Route!.Code,
            d.DeviationMinutes, d.NearStop, d.Message, d.TimestampUtc)).ToList();
    }
}

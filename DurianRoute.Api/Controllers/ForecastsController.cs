using DurianRoute.Api.Data;
using DurianRoute.Api.Forecasting;
using DurianRoute.Api.Traffic;
using DurianRoute.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DurianRoute.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/forecasts")]
public class ForecastsController(DurianDbContext db, TrafficModelService model) : ControllerBase
{
    /// <summary>Forecast for the next 24 hours, evaluated against the normal lane layout.</summary>
    [HttpGet]
    public async Task<List<ForecastDto>> Next24Hours(CancellationToken ct)
    {
        var first = DavaoCalendar.CurrentHourUtc();
        var last = first.AddHours(ForecastWorker.HorizonHours - 1);
        var rows = await db.Forecasts.AsNoTracking().Include(f => f.ChokePoint)
            .Where(f => f.HourStartUtc >= first && f.HourStartUtc <= last)
            .OrderBy(f => f.HourStartUtc).ThenBy(f => f.ChokePointId)
            .ToListAsync(ct);

        return rows.Select(f =>
        {
            var cp = f.ChokePoint!;
            var vc = TrafficMath.VolumeToCapacity(f.PredictedVolume, cp.LanesPerDirection, cp.LaneCapacityVph);
            return new ForecastDto(cp.Id, cp.Name, f.HourStartUtc, f.Direction,
                f.PredictedVolume, Math.Round(vc, 2), Math.Round(TrafficMath.BprDelay(cp.FreeFlowMinutes, vc), 1),
                f.PredictedBusPassengers, f.GeneratedAtUtc);
        }).ToList();
    }

    /// <summary>Forecast vs observed volume for the last 48 hours plus the forecast ahead.</summary>
    [HttpGet("accuracy")]
    public async Task<List<ForecastVsActualDto>> Accuracy(int chokePointId, TravelDirection direction, CancellationToken ct)
    {
        var current = DavaoCalendar.CurrentHourUtc();
        var from = current.AddHours(-48);
        var to = current.AddHours(ForecastWorker.HorizonHours);

        var forecasts = await db.Forecasts.AsNoTracking()
            .Where(f => f.ChokePointId == chokePointId && f.Direction == direction && f.HourStartUtc >= from && f.HourStartUtc < to)
            .ToDictionaryAsync(f => f.HourStartUtc, f => f.PredictedVolume, ct);
        var actuals = await db.TrafficObservations.AsNoTracking()
            .Where(o => o.ChokePointId == chokePointId && o.Direction == direction && o.HourStartUtc >= from && o.HourStartUtc < to)
            .ToDictionaryAsync(o => o.HourStartUtc, o => o.VehicleVolume, ct);

        var result = new List<ForecastVsActualDto>();
        for (var h = from; h < to; h = h.AddHours(1))
            result.Add(new ForecastVsActualDto(h,
                forecasts.TryGetValue(h, out var f) ? f : null,
                actuals.TryGetValue(h, out var a) ? a : null));
        return result;
    }

    [HttpGet("metrics")]
    public async Task<ActionResult<ModelMetricsDto>> Metrics(CancellationToken ct)
    {
        var m = await db.ModelMetrics.AsNoTracking().OrderByDescending(x => x.TrainedAtUtc).FirstOrDefaultAsync(ct);
        return m is null ? NoContent() : ToDto(m);
    }

    [HttpPost("retrain")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<ModelMetricsDto> Retrain(CancellationToken ct) => ToDto(await model.TrainAsync(db, ct));

    private static ModelMetricsDto ToDto(ModelMetrics m) => new(
        m.TrainedAtUtc, m.TrainingRows, m.TestRows,
        Math.Round(m.Mae, 1), Math.Round(m.Rmse, 1), Math.Round(m.RSquared, 3),
        Math.Round(m.BaselineMae, 1), Math.Round(m.BaselineRmse, 1));
}

using DurianRoute.Api.Data;
using DurianRoute.Api.Traffic;
using DurianRoute.Shared;
using Microsoft.EntityFrameworkCore;

namespace DurianRoute.Api.Forecasting;

/// <summary>
/// Hourly pipeline: (1) record the traffic observed in completed hours, (2) train the model when
/// missing or older than a day, (3) forecast the next 24 hours for every choke point. Each hour's
/// forecast is written once, so stored forecasts were made up to 24 hours in advance and can be
/// compared with what actually happened.
/// </summary>
public class ForecastWorker(
    IServiceScopeFactory scopes,
    TrafficModelService model,
    IConfiguration config,
    ILogger<ForecastWorker> logger) : BackgroundService
{
    public const int HorizonHours = 24;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        model.TryLoad();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Forecast pipeline failed");
            }

            // Run again just after the next hour boundary.
            var next = DavaoCalendar.CurrentHourUtc().AddHours(1).AddMinutes(1);
            await Task.Delay(next - DateTime.UtcNow, stoppingToken);
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DurianDbContext>();

        await BackfillObservationsAsync(db, ct);

        var lastTrained = await db.ModelMetrics.OrderByDescending(m => m.TrainedAtUtc)
            .Select(m => (DateTime?)m.TrainedAtUtc).FirstOrDefaultAsync(ct);
        if (!model.IsReady || lastTrained is null || DateTime.UtcNow - lastTrained > TimeSpan.FromHours(24))
            await model.TrainAsync(db, ct);

        var created = await GenerateForecastsAsync(db, ct);
        if (created > 0) logger.LogInformation("Generated {Count} hourly forecasts", created);
    }

    /// <summary>Stores observed traffic for every completed hour not yet recorded.</summary>
    private async Task BackfillObservationsAsync(DurianDbContext db, CancellationToken ct)
    {
        var historyDays = config.GetValue("Forecasting:HistoryDays", 180);
        var endExclusive = DavaoCalendar.CurrentHourUtc();
        var last = await db.TrafficObservations.MaxAsync(o => (DateTime?)o.HourStartUtc, ct);
        var start = last?.AddHours(1) ?? endExclusive.AddDays(-historyDays);
        if (start >= endExclusive) return;

        var chokePoints = await db.ChokePoints.AsNoTracking().ToListAsync(ct);
        db.ChangeTracker.AutoDetectChangesEnabled = false;

        var total = 0;
        for (var dayStart = start; dayStart < endExclusive; dayStart = dayStart.AddDays(1))
        {
            var dayEnd = dayStart.AddDays(1) < endExclusive ? dayStart.AddDays(1) : endExclusive;
            for (var hour = dayStart; hour < dayEnd; hour = hour.AddHours(1))
                foreach (var cp in chokePoints)
                    foreach (var dir in Enum.GetValues<TravelDirection>())
                    {
                        var s = SyntheticTraffic.Sample(cp, hour, dir);
                        db.TrafficObservations.Add(new TrafficObservation
                        {
                            ChokePointId = cp.Id,
                            HourStartUtc = hour,
                            Direction = dir,
                            VehicleVolume = s.VehicleVolume,
                            BusPassengers = s.BusPassengers,
                            RainMm = s.RainMm,
                            IsHoliday = s.IsHoliday
                        });
                        total++;
                    }

            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }

        db.ChangeTracker.AutoDetectChangesEnabled = true;
        logger.LogInformation("Recorded {Count} hourly traffic observations", total);
    }

    private async Task<int> GenerateForecastsAsync(DurianDbContext db, CancellationToken ct)
    {
        var first = DavaoCalendar.CurrentHourUtc();
        var last = first.AddHours(HorizonHours - 1);

        var existing = (await db.Forecasts.AsNoTracking()
                .Where(f => f.HourStartUtc >= first && f.HourStartUtc <= last)
                .Select(f => new { f.ChokePointId, f.HourStartUtc, f.Direction })
                .ToListAsync(ct))
            .Select(f => (f.ChokePointId, f.HourStartUtc, f.Direction))
            .ToHashSet();

        var history = await db.TrafficObservations.AsNoTracking()
            .Where(o => o.HourStartUtc >= first.AddHours(-168) && o.HourStartUtc < first)
            .Select(o => new { o.ChokePointId, o.HourStartUtc, o.Direction, o.VehicleVolume, o.BusPassengers })
            .ToListAsync(ct);
        var lookup = history.ToDictionary(o => (o.ChokePointId, o.HourStartUtc, o.Direction));

        var chokePointIds = await db.ChokePoints.Select(c => c.Id).ToListAsync(ct);
        var now = DateTime.UtcNow;
        var created = 0;

        for (var hour = first; hour <= last; hour = hour.AddHours(1))
            foreach (var cpId in chokePointIds)
                foreach (var dir in Enum.GetValues<TravelDirection>())
                {
                    if (existing.Contains((cpId, hour, dir))) continue;
                    if (!lookup.TryGetValue((cpId, hour.AddHours(-24), dir), out var lag24)) continue;
                    if (!lookup.TryGetValue((cpId, hour.AddHours(-168), dir), out var lag168)) continue;

                    var input = TrafficModelInput.Create(cpId, hour, dir, lag24.VehicleVolume, lag168.VehicleVolume);
                    db.Forecasts.Add(new TrafficForecast
                    {
                        ChokePointId = cpId,
                        HourStartUtc = hour,
                        Direction = dir,
                        PredictedVolume = Math.Round(model.Predict(input), 1),
                        PredictedBusPassengers = Math.Round((lag24.BusPassengers + lag168.BusPassengers) / 2, 1),
                        GeneratedAtUtc = now
                    });
                    created++;
                }

        await db.SaveChangesAsync(ct);
        return created;
    }
}

using DurianRoute.Api.Data;
using DurianRoute.Api.Forecasting;
using DurianRoute.Api.Traffic;
using DurianRoute.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace DurianRoute.Tests;

/// <summary>Trains the real ML.NET pipeline on five weeks of synthetic data and checks its metrics.</summary>
public class TrafficModelTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly string _contentRoot = Path.Combine(Path.GetTempPath(), "durianroute-tests-" + Guid.NewGuid().ToString("N"));
    private DurianDbContext _db = null!;
    private TrafficModelService _model = null!;
    private ModelMetrics _metrics = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _db = new DurianDbContext(new DbContextOptionsBuilder<DurianDbContext>().UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();

        var chokePoints = new[] { TestData.ChokePoint(1), TestData.ChokePoint(2, lanes: 2, reversible: false) };
        foreach (var cp in chokePoints) cp.Id = 0;   // let the database assign ids
        _db.ChokePoints.AddRange(chokePoints);
        await _db.SaveChangesAsync();

        var start = new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);
        for (var hour = start; hour < start.AddDays(35); hour = hour.AddHours(1))
            foreach (var cp in chokePoints)
                foreach (var dir in Enum.GetValues<TravelDirection>())
                {
                    var s = SyntheticTraffic.Sample(cp, hour, dir);
                    _db.TrafficObservations.Add(new TrafficObservation
                    {
                        ChokePointId = cp.Id, HourStartUtc = hour, Direction = dir,
                        VehicleVolume = s.VehicleVolume, BusPassengers = s.BusPassengers
                    });
                }
        await _db.SaveChangesAsync();

        _model = new TrafficModelService(new FakeEnvironment(_contentRoot), NullLogger<TrafficModelService>.Instance);
        _metrics = await _model.TrainAsync(_db, CancellationToken.None);
    }

    [Fact]
    public void Model_explains_most_of_the_variance()
    {
        Assert.True(_metrics.RSquared > 0.9, $"R² was {_metrics.RSquared:F3}");
    }

    [Fact]
    public void Model_beats_the_same_hour_last_week_baseline()
    {
        Assert.True(_metrics.Mae < _metrics.BaselineMae, $"MAE {_metrics.Mae:F1} vs baseline {_metrics.BaselineMae:F1}");
        Assert.True(_metrics.Rmse < _metrics.BaselineRmse, $"RMSE {_metrics.Rmse:F1} vs baseline {_metrics.BaselineRmse:F1}");
    }

    [Fact]
    public void Evaluation_uses_a_time_based_holdout()
    {
        Assert.True(_metrics.TestRows > 0);
        Assert.True(_metrics.TrainingRows > _metrics.TestRows / 2);
    }

    [Fact]
    public async Task Metrics_are_persisted_and_the_model_is_saved_and_reloadable()
    {
        Assert.Equal(1, await _db.ModelMetrics.CountAsync());
        Assert.True(File.Exists(Path.Combine(_contentRoot, "App_Data", "traffic-model.zip")));

        var reloaded = new TrafficModelService(new FakeEnvironment(_contentRoot), NullLogger<TrafficModelService>.Instance);
        Assert.True(reloaded.TryLoad());
        Assert.True(reloaded.IsReady);
    }

    [Fact]
    public void Predicts_a_heavier_morning_inbound_peak_than_night()
    {
        var cpId = _db.ChokePoints.First().Id;
        var peakHour = DavaoCalendar.ToUtc(new DateTime(2026, 10, 7, 7, 0, 0));
        var nightHour = DavaoCalendar.ToUtc(new DateTime(2026, 10, 7, 3, 0, 0));
        var cp = _db.ChokePoints.First();

        float Predict(DateTime hour) => _model.Predict(TrafficModelInput.Create(cpId, hour, TravelDirection.Inbound,
            SyntheticTraffic.Sample(cp, hour.AddHours(-24), TravelDirection.Inbound).VehicleVolume,
            SyntheticTraffic.Sample(cp, hour.AddHours(-168), TravelDirection.Inbound).VehicleVolume));

        Assert.True(Predict(peakHour) > 3 * Predict(nightHour));
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
        try { Directory.Delete(_contentRoot, recursive: true); } catch (IOException) { }
    }

    private sealed class FakeEnvironment(string contentRoot) : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = contentRoot;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "DurianRoute.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRoot;
        public string EnvironmentName { get; set; } = "Testing";
    }
}

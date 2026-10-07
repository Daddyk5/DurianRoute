using DurianRoute.Api.Data;
using DurianRoute.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.ML;

namespace DurianRoute.Api.Forecasting;

/// <summary>
/// Trains, evaluates, persists and serves the ML.NET FastTree regression that predicts hourly
/// vehicle volume per choke point and direction. Thread-safe singleton.
/// </summary>
public class TrafficModelService(IWebHostEnvironment env, ILogger<TrafficModelService> logger)
{
    private const int TestDays = 14;

    private readonly MLContext _ml = new(seed: 42);
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _trainLock = new(1, 1);
    private PredictionEngine<TrafficModelInput, TrafficModelOutput>? _engine;

    private string ModelPath => Path.Combine(env.ContentRootPath, "App_Data", "traffic-model.zip");

    public bool IsReady
    {
        get { lock (_gate) return _engine is not null; }
    }

    public bool TryLoad()
    {
        if (!File.Exists(ModelPath)) return false;
        try
        {
            var model = _ml.Model.Load(ModelPath, out _);
            SwapModel(model);
            logger.LogInformation("Loaded traffic model from {Path}", ModelPath);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not load traffic model, it will be retrained");
            return false;
        }
    }

    public float Predict(TrafficModelInput input)
    {
        lock (_gate)
        {
            if (_engine is null) throw new InvalidOperationException("Traffic model is not trained yet.");
            return Math.Max(0, _engine.Predict(input).PredictedVolume);
        }
    }

    /// <summary>
    /// Trains on all history except the last <see cref="TestDays"/> days, evaluates on those days
    /// against a "same hour last week" baseline, then refits on everything and saves the model.
    /// </summary>
    public async Task<ModelMetrics> TrainAsync(DurianDbContext db, CancellationToken ct)
    {
        await _trainLock.WaitAsync(ct);
        try
        {
            var observations = await db.TrafficObservations.AsNoTracking()
                .Select(o => new { o.ChokePointId, o.HourStartUtc, o.Direction, o.VehicleVolume })
                .ToListAsync(ct);

            var lookup = observations.ToDictionary(o => (o.ChokePointId, o.HourStartUtc, o.Direction), o => o.VehicleVolume);
            var rows = new List<(DateTime Hour, TrafficModelInput Input)>(observations.Count);
            foreach (var o in observations)
            {
                if (!lookup.TryGetValue((o.ChokePointId, o.HourStartUtc.AddHours(-24), o.Direction), out var lag24)) continue;
                if (!lookup.TryGetValue((o.ChokePointId, o.HourStartUtc.AddHours(-168), o.Direction), out var lag168)) continue;
                rows.Add((o.HourStartUtc, TrafficModelInput.Create(o.ChokePointId, o.HourStartUtc, o.Direction, lag24, lag168, o.VehicleVolume)));
            }

            if (rows.Count < 1000)
                throw new InvalidOperationException($"Not enough history to train ({rows.Count} rows).");

            var cutoff = rows.Max(r => r.Hour).AddDays(-TestDays);
            var train = rows.Where(r => r.Hour <= cutoff).Select(r => r.Input).ToList();
            var test = rows.Where(r => r.Hour > cutoff).Select(r => r.Input).ToList();

            var pipeline = BuildPipeline();
            var evalModel = pipeline.Fit(_ml.Data.LoadFromEnumerable(train));
            var predictions = evalModel.Transform(_ml.Data.LoadFromEnumerable(test));
            var m = _ml.Regression.Evaluate(predictions, labelColumnName: "Label", scoreColumnName: "Score");

            var baselineErrors = test.Select(t => (double)(t.VehicleVolume - t.Lag168)).ToList();
            var metrics = new ModelMetrics
            {
                TrainedAtUtc = DateTime.UtcNow,
                TrainingRows = train.Count,
                TestRows = test.Count,
                Mae = m.MeanAbsoluteError,
                Rmse = m.RootMeanSquaredError,
                RSquared = m.RSquared,
                BaselineMae = baselineErrors.Average(Math.Abs),
                BaselineRmse = Math.Sqrt(baselineErrors.Average(e => e * e))
            };

            // Final model uses every row so the most recent weeks inform tomorrow's forecast.
            var finalModel = pipeline.Fit(_ml.Data.LoadFromEnumerable(rows.Select(r => r.Input)));
            Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
            _ml.Model.Save(finalModel, _ml.Data.LoadFromEnumerable(train.Take(1)).Schema, ModelPath);
            SwapModel(finalModel);

            db.ModelMetrics.Add(metrics);
            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "Trained traffic model: MAE {Mae:F1}, RMSE {Rmse:F1}, R² {R2:F3} (baseline MAE {BMae:F1})",
                metrics.Mae, metrics.Rmse, metrics.RSquared, metrics.BaselineMae);
            return metrics;
        }
        finally
        {
            _trainLock.Release();
        }
    }

    private IEstimator<ITransformer> BuildPipeline() =>
        _ml.Transforms.Categorical.OneHotEncoding("ChokePointEncoded", nameof(TrafficModelInput.ChokePoint))
            .Append(_ml.Transforms.Concatenate("Features", ["ChokePointEncoded", .. TrafficModelInput.FeatureColumns]))
            .Append(_ml.Regression.Trainers.FastTree(
                labelColumnName: "Label",
                featureColumnName: "Features",
                numberOfLeaves: 32,
                numberOfTrees: 200,
                minimumExampleCountPerLeaf: 20,
                learningRate: 0.1));

    private void SwapModel(ITransformer model)
    {
        var engine = _ml.Model.CreatePredictionEngine<TrafficModelInput, TrafficModelOutput>(model);
        lock (_gate)
        {
            _engine?.Dispose();
            _engine = engine;
        }
    }
}

using DurianRoute.Shared;

namespace DurianRoute.Client.Services;

public enum ActivityKind { Late, Early, Lane, Weather, Incident, Plan, Command, Request }

public record ActivityItem(DateTime TimestampUtc, ActivityKind Kind, string Title, string Detail);

/// <summary>City-wide congestion summary derived from the live choke point feed.</summary>
public record TrafficIndex(double Value, string Label, string WorstChokePoint, double WorstVc);

/// <summary>
/// Shared live picture of the network, fed by the SignalR telemetry stream. Pages read from it and
/// listen to <see cref="Changed"/>, which is coalesced to at most two notifications per second.
/// </summary>
public sealed class LiveStore : IDisposable
{
    private const int MaxActivity = 60;
    private const int MaxHistory = 90;          // 15 minutes of 10-second traffic samples

    private readonly TelemetryClient _telemetry;
    private readonly Timer _flush;
    private readonly Dictionary<int, BusPositionDto> _buses = [];
    private readonly Dictionary<int, bool> _incidents = [];
    private readonly LinkedList<ActivityItem> _activity = new();
    private readonly List<double> _indexHistory = [];
    private volatile bool _dirty;

    public LiveStore(TelemetryClient telemetry)
    {
        _telemetry = telemetry;
        _telemetry.BusPositions += OnPositions;
        _telemetry.ChokePointStatus += OnStatus;
        _telemetry.Weather += OnWeather;
        _telemetry.DeviationAlert += OnAlert;
        _telemetry.LaneConfigChanged += OnLaneChanged;
        _telemetry.RecommendationsUpdated += OnPlan;
        _telemetry.LaneRequestSubmitted += OnRequestSubmitted;
        _telemetry.LaneRequestDecided += OnRequestDecided;
        _flush = new Timer(_ => { if (_dirty) { _dirty = false; Changed?.Invoke(); } }, null, 500, 500);
    }

    public event Action? Changed;

    public IReadOnlyCollection<BusPositionDto> Buses => _buses.Values;
    public List<ChokePointStatusDto> ChokePoints { get; private set; } = [];
    public WeatherDto? Weather { get; private set; }
    public IEnumerable<ActivityItem> Activity => _activity;
    public IReadOnlyList<double> IndexHistory => _indexHistory;
    public TrafficIndex? Traffic { get; private set; }

    public BusPositionDto? Bus(int id) => _buses.TryGetValue(id, out var b) ? b : null;

    public static bool IsLate(BusPositionDto b) => b.DeviationMinutes > 5;
    public static bool IsEarly(BusPositionDto b) => b.DeviationMinutes < -4;
    public static bool IsBunching(BusPositionDto b) => b.HeadwayMinutes is < 3;

    public void Seed(List<ChokePointStatusDto> status, WeatherDto? weather)
    {
        if (status.Count > 0) OnStatus(status);
        if (weather is not null && Weather is null) Weather = weather;
        _dirty = true;
    }

    public void Log(ActivityKind kind, string title, string detail = "")
    {
        _activity.AddFirst(new ActivityItem(DateTime.UtcNow, kind, title, detail));
        while (_activity.Count > MaxActivity) _activity.RemoveLast();
        _dirty = true;
    }

    private void OnPositions(List<BusPositionDto> positions)
    {
        foreach (var p in positions) _buses[p.BusId] = p;
        _dirty = true;
    }

    private void OnStatus(List<ChokePointStatusDto> status)
    {
        ChokePoints = status;
        foreach (var s in status)
        {
            var was = _incidents.GetValueOrDefault(s.ChokePointId);
            if (s.IncidentActive && !was) Log(ActivityKind.Incident, $"Incident at {s.Name}", "Capacity cut by 40% until cleared");
            _incidents[s.ChokePointId] = s.IncidentActive;
        }

        if (status.Count > 0)
        {
            var worst = status.MaxBy(s => Math.Max(s.InboundVc, s.OutboundVc))!;
            var avg = status.Average(s => Math.Max(s.InboundVc, s.OutboundVc));
            var value = Math.Round(Math.Clamp(avg / 1.2 * 10, 0, 10), 1);
            var label = value switch { < 3 => "Light", < 5 => "Moderate", < 7 => "Heavy", _ => "Severe" };
            Traffic = new TrafficIndex(value, label, worst.Name, Math.Max(worst.InboundVc, worst.OutboundVc));
            _indexHistory.Add(value);
            if (_indexHistory.Count > MaxHistory) _indexHistory.RemoveAt(0);
        }
        _dirty = true;
    }

    private void OnWeather(WeatherDto weather)
    {
        if (Weather is not null && Weather.Condition != weather.Condition)
            Log(ActivityKind.Weather, $"Weather: {weather.Condition}", weather.RoadImpact);
        Weather = weather;
        _dirty = true;
    }

    private void OnAlert(DeviationAlertDto alert) =>
        Log(alert.DeviationMinutes > 0 ? ActivityKind.Late : ActivityKind.Early,
            alert.Message, $"{alert.RouteCode} · near {alert.NearStop}");

    private void OnLaneChanged(LaneConfigChangedDto change) =>
        Log(ActivityKind.Lane, $"{change.ChokePointName}: {Format.Lane(change.State)}", $"by {change.Actor}");

    private void OnPlan() => Log(ActivityKind.Plan, "Lane plan updated", "Recommendations changed");

    private void OnRequestSubmitted(LaneRecommendationDto r) =>
        Log(ActivityKind.Request, $"{r.RequestedBy} requested {Format.Lane(r.State)}", $"{r.ChokePointName} · waiting for admin");

    private void OnRequestDecided(LaneRecommendationDto r) =>
        Log(ActivityKind.Request, $"Request {(r.Status == RecommendationStatus.Rejected ? "rejected" : "approved")}: {r.ChokePointName}",
            $"{Format.Lane(r.State)} · by {r.DecidedBy}");

    public void Clear()
    {
        _buses.Clear();
        _activity.Clear();
        _indexHistory.Clear();
        ChokePoints = [];
        Traffic = null;
        _dirty = true;
    }

    public void Dispose()
    {
        _flush.Dispose();
        _telemetry.BusPositions -= OnPositions;
        _telemetry.ChokePointStatus -= OnStatus;
        _telemetry.Weather -= OnWeather;
        _telemetry.DeviationAlert -= OnAlert;
        _telemetry.LaneConfigChanged -= OnLaneChanged;
        _telemetry.RecommendationsUpdated -= OnPlan;
        _telemetry.LaneRequestSubmitted -= OnRequestSubmitted;
        _telemetry.LaneRequestDecided -= OnRequestDecided;
    }
}

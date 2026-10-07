using DurianRoute.Api.Data;
using DurianRoute.Api.Traffic;
using DurianRoute.Shared;

namespace DurianRoute.Api.Simulation;

public record DeviationEvent(int BusId, string PlateNumber, string RouteCode, double DeviationMinutes, string NearStop, string Message);

/// <summary>
/// Simulated public utility buses moving along their routes. Thread-safe singleton; the
/// <see cref="BusSimulator"/> advances it and the hub issues dispatcher commands against it.
/// </summary>
public class FleetState
{
    private const double CruiseSpeedMps = 8.33;     // 30 km/h in free flow
    private const double PlannedSpeedMps = 6.67;    // 24 km/h timetable speed, dwell time included
    private const double LateThresholdMinutes = 5;
    private const double EarlyThresholdMinutes = -4;

    private readonly Lock _gate = new();
    private readonly Dictionary<int, RouteGeometry> _routes = [];
    private readonly Dictionary<int, BusSim> _buses = [];

    private sealed class RouteGeometry
    {
        public required int Id { get; init; }
        public required string Code { get; init; }
        public required List<Stop> Stops { get; init; }
        public required double[] StopDistance { get; init; }   // cumulative meters from stop 0
        public double Length => StopDistance[^1];
    }

    private sealed class BusSim
    {
        public required int BusId { get; init; }
        public required string Plate { get; init; }
        public required RouteGeometry Route { get; init; }
        public TravelDirection Direction { get; set; }
        public double DistanceM { get; set; }               // along the current trip
        public double SimElapsedSeconds { get; set; }       // since trip departure
        public int NextStopIndex { get; set; }              // index into trip-ordered stops
        public double DwellRemaining { get; set; }
        public double LayoverRemaining { get; set; }
        public DateTime? HeldUntilUtc { get; set; }
        public double SpeedMps { get; set; }
        public double DeviationMinutes { get; set; }
        public int AlertBucket { get; set; }                // -1 early, 0 on time, 1 late
        public BusStatus Status { get; set; }
    }

    public void Initialize(IEnumerable<BusRoute> routes)
    {
        lock (_gate)
        {
            _routes.Clear();
            _buses.Clear();
            foreach (var route in routes)
            {
                var stops = route.Stops.OrderBy(s => s.Sequence).ToList();
                if (stops.Count < 2) continue;

                var cumulative = new double[stops.Count];
                for (var i = 1; i < stops.Count; i++)
                    cumulative[i] = cumulative[i - 1] + Geo.DistanceMeters(stops[i - 1].Lat, stops[i - 1].Lng, stops[i].Lat, stops[i].Lng);

                var geometry = new RouteGeometry { Id = route.Id, Code = route.Code, Stops = stops, StopDistance = cumulative };
                _routes[route.Id] = geometry;

                // Stagger the fleet along the route, alternating directions.
                var buses = route.Buses.OrderBy(b => b.Id).ToList();
                for (var i = 0; i < buses.Count; i++)
                {
                    var sim = new BusSim
                    {
                        BusId = buses[i].Id,
                        Plate = buses[i].PlateNumber,
                        Route = geometry,
                        Direction = i % 2 == 0 ? TravelDirection.Inbound : TravelDirection.Outbound,
                        DistanceM = geometry.Length * (i / 2) / Math.Max(1, (buses.Count + 1) / 2) * 0.9
                    };
                    sim.SimElapsedSeconds = sim.DistanceM / PlannedSpeedMps;
                    sim.NextStopIndex = NextStopAfter(geometry, sim.Direction, sim.DistanceM);
                    _buses[sim.BusId] = sim;
                }
            }
        }
    }

    public bool Hold(int busId, int minutes, DateTime nowUtc)
    {
        lock (_gate)
        {
            if (!_buses.TryGetValue(busId, out var bus)) return false;
            bus.HeldUntilUtc = nowUtc.AddMinutes(Math.Clamp(minutes, 1, 30));
            return true;
        }
    }

    public bool Release(int busId)
    {
        lock (_gate)
        {
            if (!_buses.TryGetValue(busId, out var bus)) return false;
            bus.HeldUntilUtc = null;
            return true;
        }
    }

    public IReadOnlyCollection<int> RouteIds
    {
        get { lock (_gate) return _routes.Keys.ToList(); }
    }

    /// <summary>Advances every bus by <paramref name="simSeconds"/> of simulated time.</summary>
    public (List<BusPositionDto> Positions, List<DeviationEvent> Events) Tick(
        DateTime nowUtc, double simSeconds, LiveTrafficState traffic, Random rng)
    {
        var positions = new List<BusPositionDto>();
        var events = new List<DeviationEvent>();

        lock (_gate)
        {
            foreach (var bus in _buses.Values)
            {
                Advance(bus, nowUtc, simSeconds, traffic, rng);

                var (lat, lng, heading) = Locate(bus);
                var nextStop = bus.NextStopIndex < bus.Route.Stops.Count
                    ? TripStop(bus.Route, bus.Direction, bus.NextStopIndex).Name
                    : "Terminal";

                positions.Add(new BusPositionDto(
                    bus.BusId, bus.Plate, bus.Route.Id, bus.Route.Code,
                    Math.Round(lat, 6), Math.Round(lng, 6), Math.Round(heading),
                    Math.Round(bus.SpeedMps * 3.6, 1), bus.Direction, nextStop,
                    Math.Round(bus.DeviationMinutes, 1), bus.Status, nowUtc));

                var bucket = bus.DeviationMinutes > LateThresholdMinutes ? 1
                    : bus.DeviationMinutes < EarlyThresholdMinutes ? -1 : 0;
                if (bucket != bus.AlertBucket)
                {
                    bus.AlertBucket = bucket;
                    if (bucket != 0)
                    {
                        var dev = Math.Round(bus.DeviationMinutes, 1);
                        var message = bucket > 0
                            ? $"{bus.Plate} is running {dev} min behind schedule"
                            : $"{bus.Plate} is running {Math.Abs(dev)} min ahead of schedule (bunching risk)";
                        events.Add(new DeviationEvent(bus.BusId, bus.Plate, bus.Route.Code, dev, nextStop, message));
                    }
                }
            }
        }

        return (positions, events);
    }

    private static void Advance(BusSim bus, DateTime nowUtc, double dt, LiveTrafficState traffic, Random rng)
    {
        if (bus.LayoverRemaining > 0)
        {
            bus.LayoverRemaining -= dt;
            bus.SpeedMps = 0;
            bus.Status = BusStatus.Layover;
            if (bus.LayoverRemaining <= 0)
            {
                // Start the return trip on a fresh timetable.
                bus.Direction = bus.Direction == TravelDirection.Inbound ? TravelDirection.Outbound : TravelDirection.Inbound;
                bus.DistanceM = 0;
                bus.SimElapsedSeconds = 0;
                bus.NextStopIndex = 1;
                bus.DeviationMinutes = 0;
            }
            return;
        }

        bus.SimElapsedSeconds += dt;

        if (bus.HeldUntilUtc > nowUtc)
        {
            bus.SpeedMps = 0;
            bus.Status = BusStatus.Held;
            UpdateDeviation(bus);
            return;
        }
        bus.HeldUntilUtc = null;

        if (bus.DwellRemaining > 0)
        {
            bus.DwellRemaining -= dt;
            bus.SpeedMps = 0;
            bus.Status = BusStatus.AtStop;
            UpdateDeviation(bus);
            return;
        }

        var (lat, lng, _) = Locate(bus);
        var factor = traffic.BusSpeedFactor(lat, lng, bus.Direction);
        bus.SpeedMps = CruiseSpeedMps * factor * (0.88 + rng.NextDouble() * 0.2);
        bus.DistanceM += bus.SpeedMps * dt;
        bus.Status = BusStatus.InService;

        var stopDistance = TripStopDistance(bus.Route, bus.Direction, bus.NextStopIndex);
        if (bus.DistanceM >= stopDistance)
        {
            bus.DistanceM = stopDistance;
            bus.NextStopIndex++;
            if (bus.NextStopIndex >= bus.Route.Stops.Count)
            {
                bus.LayoverRemaining = 120;
                bus.Status = BusStatus.Layover;
            }
            else
            {
                bus.DwellRemaining = 15 + rng.NextDouble() * 25;
                bus.Status = BusStatus.AtStop;
            }
        }

        UpdateDeviation(bus);
    }

    /// <summary>Positive = late. Compares simulated elapsed time with timetable time to this point.</summary>
    private static void UpdateDeviation(BusSim bus)
    {
        var plannedSeconds = bus.DistanceM / PlannedSpeedMps;
        bus.DeviationMinutes = (bus.SimElapsedSeconds - plannedSeconds) / 60;
    }

    private static Stop TripStop(RouteGeometry r, TravelDirection d, int tripIndex) =>
        d == TravelDirection.Inbound ? r.Stops[tripIndex] : r.Stops[r.Stops.Count - 1 - tripIndex];

    private static double TripStopDistance(RouteGeometry r, TravelDirection d, int tripIndex) =>
        d == TravelDirection.Inbound
            ? r.StopDistance[tripIndex]
            : r.Length - r.StopDistance[r.Stops.Count - 1 - tripIndex];

    private static int NextStopAfter(RouteGeometry r, TravelDirection d, double distance)
    {
        for (var i = 0; i < r.Stops.Count; i++)
            if (TripStopDistance(r, d, i) > distance) return i;
        return r.Stops.Count - 1;
    }

    private static (double Lat, double Lng, double Heading) Locate(BusSim bus)
    {
        var r = bus.Route;
        var forward = bus.Direction == TravelDirection.Inbound ? bus.DistanceM : r.Length - bus.DistanceM;
        forward = Math.Clamp(forward, 0, r.Length);

        var seg = 0;
        while (seg < r.StopDistance.Length - 2 && r.StopDistance[seg + 1] < forward) seg++;

        var a = r.Stops[seg];
        var b = r.Stops[seg + 1];
        var segLength = r.StopDistance[seg + 1] - r.StopDistance[seg];
        var t = segLength <= 0 ? 0 : (forward - r.StopDistance[seg]) / segLength;

        var lat = a.Lat + (b.Lat - a.Lat) * t;
        var lng = a.Lng + (b.Lng - a.Lng) * t;
        var heading = bus.Direction == TravelDirection.Inbound
            ? Geo.BearingDegrees(a.Lat, a.Lng, b.Lat, b.Lng)
            : Geo.BearingDegrees(b.Lat, b.Lng, a.Lat, a.Lng);
        return (lat, lng, heading);
    }
}

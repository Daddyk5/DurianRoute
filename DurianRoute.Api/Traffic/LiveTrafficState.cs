using DurianRoute.Api.Data;
using DurianRoute.Shared;

namespace DurianRoute.Api.Traffic;

/// <summary>In-memory live conditions at every choke point. Thread-safe singleton.</summary>
public class LiveTrafficState
{
    private readonly Lock _gate = new();
    private readonly Dictionary<int, Entry> _entries = [];

    private sealed class Entry
    {
        public required ChokePoint ChokePoint { get; init; }
        public double InboundVolume { get; set; }
        public double OutboundVolume { get; set; }
        public DateTime? IncidentUntilUtc { get; set; }
        public ChokePointStatusDto? Status { get; set; }
    }

    public void Initialize(IEnumerable<ChokePoint> chokePoints)
    {
        lock (_gate)
        {
            _entries.Clear();
            foreach (var cp in chokePoints)
                _entries[cp.Id] = new Entry { ChokePoint = cp };
        }
    }

    public IReadOnlyList<ChokePoint> ChokePoints
    {
        get { lock (_gate) return _entries.Values.Select(e => e.ChokePoint).ToList(); }
    }

    public LaneState GetLaneState(int chokePointId)
    {
        lock (_gate) return _entries.TryGetValue(chokePointId, out var e) ? e.ChokePoint.ActiveLaneState : LaneState.Normal;
    }

    public void SetLaneState(int chokePointId, LaneState state)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(chokePointId, out var e))
                e.ChokePoint.ActiveLaneState = state;
        }
    }

    public double LiveVolume(int chokePointId, TravelDirection direction)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(chokePointId, out var e)) return 0;
            return direction == TravelDirection.Inbound ? e.InboundVolume : e.OutboundVolume;
        }
    }

    public double CapacityFactor(int chokePointId, DateTime nowUtc)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(chokePointId, out var e) && e.IncidentUntilUtc > nowUtc ? 0.6 : 1.0;
        }
    }

    /// <summary>Advances the live state by one tick and returns the new statuses.</summary>
    public List<ChokePointStatusDto> Update(DateTime nowUtc, Random rng)
    {
        var hour = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, nowUtc.Hour, 0, 0, DateTimeKind.Utc);
        var result = new List<ChokePointStatusDto>();

        lock (_gate)
        {
            foreach (var e in _entries.Values)
            {
                var cp = e.ChokePoint;
                e.InboundVolume = SyntheticTraffic.Sample(cp, hour, TravelDirection.Inbound).VehicleVolume * Jitter(rng);
                e.OutboundVolume = SyntheticTraffic.Sample(cp, hour, TravelDirection.Outbound).VehicleVolume * Jitter(rng);

                // Random incidents (stalled vehicle, flooding, collision) cut capacity for 20–50 minutes.
                if (!(e.IncidentUntilUtc > nowUtc) && rng.NextDouble() < 0.0015)
                    e.IncidentUntilUtc = nowUtc.AddMinutes(rng.Next(20, 51));

                var incident = e.IncidentUntilUtc > nowUtc;
                var capFactor = incident ? 0.6 : 1.0;
                var alloc = TrafficMath.Allocate(cp, cp.ActiveLaneState);
                var vcIn = TrafficMath.VolumeToCapacity(e.InboundVolume, alloc.InboundCarLanes, cp.LaneCapacityVph, capFactor);
                var vcOut = TrafficMath.VolumeToCapacity(e.OutboundVolume, alloc.OutboundCarLanes, cp.LaneCapacityVph, capFactor);

                e.Status = new ChokePointStatusDto(
                    cp.Id, cp.Name,
                    Math.Round(e.InboundVolume), Math.Round(e.OutboundVolume),
                    Math.Round(vcIn, 2), Math.Round(vcOut, 2),
                    Math.Round(TrafficMath.BprDelay(cp.FreeFlowMinutes, vcIn), 1),
                    Math.Round(TrafficMath.BprDelay(cp.FreeFlowMinutes, vcOut), 1),
                    incident, cp.ActiveLaneState, nowUtc);
                result.Add(e.Status);
            }
        }

        return result;
    }

    public List<ChokePointStatusDto> Snapshot()
    {
        lock (_gate) return _entries.Values.Where(e => e.Status is not null).Select(e => e.Status!).ToList();
    }

    /// <summary>
    /// Speed multiplier (0–1] for a bus at the given position: buses slow down inside congested
    /// choke points unless a bus lane is active in their direction.
    /// </summary>
    public double BusSpeedFactor(double lat, double lng, TravelDirection direction)
    {
        lock (_gate)
        {
            foreach (var e in _entries.Values)
            {
                if (e.Status is null) continue;
                if (Geo.DistanceMeters(lat, lng, e.ChokePoint.Lat, e.ChokePoint.Lng) > 450) continue;

                var alloc = TrafficMath.Allocate(e.ChokePoint, e.ChokePoint.ActiveLaneState);
                if (alloc.BusLane(direction)) return 0.95;

                var vc = direction == TravelDirection.Inbound ? e.Status.InboundVc : e.Status.OutboundVc;
                var t0 = e.ChokePoint.FreeFlowMinutes;
                return Math.Clamp(t0 / (t0 + TrafficMath.BprDelay(t0, vc)), 0.12, 1.0);
            }
        }
        return 1.0;
    }

    private static double Jitter(Random rng) => 0.97 + rng.NextDouble() * 0.06;
}

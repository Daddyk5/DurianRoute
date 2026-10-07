using DurianRoute.Api.Data;
using DurianRoute.Shared;

namespace DurianRoute.Api.Traffic;

/// <summary>How the lanes at a choke point are split for a given <see cref="LaneState"/>.</summary>
public readonly record struct LaneAllocation(int InboundCarLanes, int OutboundCarLanes, bool InboundBusLane, bool OutboundBusLane)
{
    public int CarLanes(TravelDirection d) => d == TravelDirection.Inbound ? InboundCarLanes : OutboundCarLanes;
    public bool BusLane(TravelDirection d) => d == TravelDirection.Inbound ? InboundBusLane : OutboundBusLane;
}

public static class TrafficMath
{
    /// <summary>Average occupants per private vehicle (car, taxi, motorcycle mix).</summary>
    public const double VehicleOccupancy = 1.5;

    /// <summary>Upper bound on v/c used in the BPR curve so oversaturation stays finite.</summary>
    public const double MaxVc = 2.0;

    public static bool IsAllowed(ChokePoint cp, LaneState state) => state switch
    {
        LaneState.Normal => true,
        LaneState.ReversibleInbound or LaneState.ReversibleOutbound => cp.HasReversibleLane && cp.LanesPerDirection >= 2,
        LaneState.BusLaneInbound or LaneState.BusLaneOutbound => cp.LanesPerDirection >= 2,
        _ => false
    };

    public static LaneAllocation Allocate(ChokePoint cp, LaneState state)
    {
        var n = cp.LanesPerDirection;
        return state switch
        {
            LaneState.ReversibleInbound => new(n + 1, n - 1, false, false),
            LaneState.ReversibleOutbound => new(n - 1, n + 1, false, false),
            LaneState.BusLaneInbound => new(n - 1, n, true, false),
            LaneState.BusLaneOutbound => new(n, n - 1, false, true),
            _ => new(n, n, false, false)
        };
    }

    public static double VolumeToCapacity(double volume, int carLanes, double laneCapacityVph, double capacityFactor = 1.0)
    {
        var capacity = Math.Max(1, carLanes) * laneCapacityVph * capacityFactor;
        return volume / capacity;
    }

    /// <summary>
    /// Bureau of Public Roads link performance function: t = t0 · (1 + α·(v/c)^β), α = 0.15, β = 4.
    /// Returns the delay (t − t0) in minutes.
    /// </summary>
    public static double BprDelay(double freeFlowMinutes, double vc)
    {
        var x = Math.Min(Math.Max(vc, 0), MaxVc);
        return freeFlowMinutes * 0.15 * Math.Pow(x, 4);
    }
}

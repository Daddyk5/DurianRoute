using DurianRoute.Api.Data;
using DurianRoute.Api.Traffic;
using DurianRoute.Shared;

namespace DurianRoute.Api.Scheduling;

/// <summary>Expected demand at a choke point for one hourly slot.</summary>
public record SlotDemand(
    DateTime SlotStartUtc,
    double InboundVolume,
    double OutboundVolume,
    double InboundBusPassengers,
    double OutboundBusPassengers,
    double CapacityFactor = 1.0);

public record LanePlan(LaneState[] States, double[] SlotCosts, double[] NormalSlotCosts, double TotalCost);

/// <summary>
/// Chooses the lane configuration of one choke point for each upcoming hour by dynamic programming.
///
/// cost(slot, state) = Σ over directions [ car volume · occupancy · car delay + bus passengers · bus delay ]
///                     + operatingCost · [state ≠ Normal]
///   car delay  = BPR(t0, v / (car lanes · lane capacity))
///   bus delay  = bus lane ? t0 · busLaneFactor : car delay + t0 · friction · min(v/c, 1.5)
///
/// best(k, s) = cost(k, s) + min over p [ best(k−1, p) + switchCost · [p ≠ s] ]
///
/// This is a shortest path over a (slots × states) trellis, the same recurrence used by the Viterbi
/// algorithm, so the plan is globally optimal for the forecast. It runs in O(slots · states²).
/// The switching cost stops the plan from flip-flopping between configurations, and the operating
/// cost (enforcers, cones) returns the layout to normal once a special configuration stops paying off.
/// </summary>
public static class LaneScheduler
{
    private static readonly LaneState[] AllStates = Enum.GetValues<LaneState>();

    public static LanePlan Optimize(
        ChokePoint cp,
        IReadOnlyList<SlotDemand> slots,
        LaneState initialState,
        Func<int, LaneState, bool> isAllowed,
        LaneSchedulerOptions options)
    {
        var k = slots.Count;
        if (k == 0) return new LanePlan([], [], [], 0);
        var s = AllStates.Length;
        var best = new double[k, s];
        var back = new int[k, s];
        var slotCost = new double[k, s];

        for (var i = 0; i < k; i++)
            for (var j = 0; j < s; j++)
            {
                var state = AllStates[j];
                slotCost[i, j] = TrafficMath.IsAllowed(cp, state) && isAllowed(i, state)
                    ? SlotCost(cp, slots[i], state, options)
                    : double.PositiveInfinity;
            }

        for (var j = 0; j < s; j++)
            best[0, j] = slotCost[0, j] + (AllStates[j] == initialState ? 0 : options.SwitchCostPersonMinutes);

        for (var i = 1; i < k; i++)
            for (var j = 0; j < s; j++)
            {
                best[i, j] = double.PositiveInfinity;
                if (double.IsPositiveInfinity(slotCost[i, j])) continue;
                for (var p = 0; p < s; p++)
                {
                    var candidate = best[i - 1, p] + (p == j ? 0 : options.SwitchCostPersonMinutes);
                    if (candidate < best[i, j])
                    {
                        best[i, j] = candidate;
                        back[i, j] = p;
                    }
                }
                best[i, j] += slotCost[i, j];
            }

        // Trace back the cheapest path.
        var path = new int[k];
        var total = double.PositiveInfinity;
        for (var j = 0; j < s; j++)
            if (best[k - 1, j] < total)
            {
                total = best[k - 1, j];
                path[k - 1] = j;
            }
        for (var i = k - 1; i > 0; i--)
            path[i - 1] = back[i, path[i]];

        var states = path.Select(j => AllStates[j]).ToArray();
        var costs = Enumerable.Range(0, k).Select(i => slotCost[i, path[i]]).ToArray();
        var normal = Enumerable.Range(0, k).Select(i => SlotCost(cp, slots[i], LaneState.Normal, options)).ToArray();
        return new LanePlan(states, costs, normal, total);
    }

    /// <summary>Total person-minutes of delay in one hour at the choke point under <paramref name="state"/>.</summary>
    public static double SlotCost(ChokePoint cp, SlotDemand d, LaneState state, LaneSchedulerOptions o)
    {
        var alloc = TrafficMath.Allocate(cp, state);
        var operating = state == LaneState.Normal ? 0 : o.ActiveLayoutCostPerHour;
        return operating
             + DirectionCost(cp, alloc, TravelDirection.Inbound, d.InboundVolume, d.InboundBusPassengers, d.CapacityFactor, o)
             + DirectionCost(cp, alloc, TravelDirection.Outbound, d.OutboundVolume, d.OutboundBusPassengers, d.CapacityFactor, o);
    }

    private static double DirectionCost(ChokePoint cp, LaneAllocation alloc, TravelDirection dir,
        double volume, double busPassengers, double capacityFactor, LaneSchedulerOptions o)
    {
        var t0 = cp.FreeFlowMinutes;
        var vc = TrafficMath.VolumeToCapacity(volume, alloc.CarLanes(dir), cp.LaneCapacityVph, capacityFactor);
        var carDelay = TrafficMath.BprDelay(t0, vc);
        var busDelay = alloc.BusLane(dir)
            ? t0 * o.BusLaneDelayFactor
            : carDelay + t0 * o.BusFrictionFactor * Math.Min(vc, 1.5);
        return volume * TrafficMath.VehicleOccupancy * carDelay + busPassengers * busDelay;
    }
}

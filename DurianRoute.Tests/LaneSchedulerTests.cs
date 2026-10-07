using DurianRoute.Api.Data;
using DurianRoute.Api.Scheduling;
using DurianRoute.Api.Traffic;
using DurianRoute.Shared;
using static DurianRoute.Tests.TestData;

namespace DurianRoute.Tests;

public class LaneSchedulerTests
{
    private static readonly Func<int, LaneState, bool> AnyState = (_, _) => true;

    [Fact]
    public void Light_traffic_keeps_the_normal_layout()
    {
        var plan = LaneScheduler.Optimize(ChokePoint(), Repeat(6, 800, 750, 300, 300), LaneState.Normal, AnyState, Options());

        Assert.All(plan.States, s => Assert.Equal(LaneState.Normal, s));
    }

    [Fact]
    public void Heavy_inbound_tidal_flow_gets_the_reversible_lane()
    {
        var plan = LaneScheduler.Optimize(ChokePoint(), Repeat(3, 2850, 1450, 2800, 1400), LaneState.Normal, AnyState, Options());

        Assert.All(plan.States, s => Assert.Equal(LaneState.ReversibleInbound, s));
        Assert.True(plan.TotalCost < plan.NormalSlotCosts.Sum(), "Plan should beat the normal layout");
    }

    [Fact]
    public void Heavy_outbound_tidal_flow_gets_the_reversible_lane_outbound()
    {
        var plan = LaneScheduler.Optimize(ChokePoint(), Repeat(3, 1450, 2850, 1400, 2800), LaneState.Normal, AnyState, Options());

        Assert.All(plan.States, s => Assert.Equal(LaneState.ReversibleOutbound, s));
    }

    [Fact]
    public void Reversible_states_are_never_used_where_no_reversible_lane_exists()
    {
        var cp = ChokePoint(reversible: false);
        var plan = LaneScheduler.Optimize(cp, Repeat(4, 2850, 1450, 2800, 1400), LaneState.Normal, AnyState, Options());

        Assert.DoesNotContain(plan.States, s => s is LaneState.ReversibleInbound or LaneState.ReversibleOutbound);
    }

    [Fact]
    public void High_bus_ridership_at_moderate_volume_gets_a_bus_lane()
    {
        var cp = ChokePoint();
        var slots = Repeat(6, 1600, 1500, 2000, 600);
        var o = Options();

        // Precondition: the bus lane really is cheaper per hour than the normal layout.
        var hourlySaving = LaneScheduler.SlotCost(cp, slots[0], LaneState.Normal, o)
                         - LaneScheduler.SlotCost(cp, slots[0], LaneState.BusLaneInbound, o);
        Assert.True(hourlySaving * slots.Count > o.SwitchCostPersonMinutes, $"saving {hourlySaving:N0}/h is too small for this test");

        var plan = LaneScheduler.Optimize(cp, slots, LaneState.Normal, AnyState, o);

        Assert.All(plan.States, s => Assert.Equal(LaneState.BusLaneInbound, s));
    }

    [Fact]
    public void Switching_cost_prevents_flip_flopping_for_a_short_spike()
    {
        var cp = ChokePoint();
        var o = Options(switchCost: 1500);
        var spike = Slot(1, 2600, 1500, 1500, 1000);
        var slots = new List<SlotDemand> { Slot(0, 900, 900, 400, 400), spike, Slot(2, 900, 900, 400, 400) };

        // Precondition: switching would help during the spike, but not by enough to pay for switching in and out.
        var saving = LaneScheduler.SlotCost(cp, spike, LaneState.Normal, o) - LaneScheduler.SlotCost(cp, spike, LaneState.ReversibleInbound, o);
        Assert.InRange(saving, 1, 2 * o.SwitchCostPersonMinutes - 1);

        var plan = LaneScheduler.Optimize(cp, slots, LaneState.Normal, AnyState, o);

        Assert.All(plan.States, s => Assert.Equal(LaneState.Normal, s));
    }

    [Fact]
    public void Same_spike_is_handled_when_switching_is_free()
    {
        var cp = ChokePoint();
        var slots = new List<SlotDemand> { Slot(0, 900, 900, 400, 400), Slot(1, 2600, 1500, 1500, 1000), Slot(2, 900, 900, 400, 400) };

        var plan = LaneScheduler.Optimize(cp, slots, LaneState.Normal, AnyState, Options(switchCost: 0));

        Assert.Equal(LaneState.ReversibleInbound, plan.States[1]);
    }

    [Fact]
    public void Locked_slots_are_respected()
    {
        var plan = LaneScheduler.Optimize(ChokePoint(), Repeat(4, 800, 800, 300, 300), LaneState.Normal,
            (i, s) => i != 2 || s == LaneState.BusLaneOutbound, Options());

        Assert.Equal(LaneState.BusLaneOutbound, plan.States[2]);
    }

    [Fact]
    public void Starting_from_a_non_normal_layout_reverts_when_demand_drops()
    {
        var cp = ChokePoint();
        var o = Options();
        var night = Repeat(12, 700, 700, 300, 300);

        // Precondition: over the quiet hours, staying reconfigured costs more than one switch back.
        var extra = night.Sum(s => LaneScheduler.SlotCost(cp, s, LaneState.ReversibleInbound, o) - LaneScheduler.SlotCost(cp, s, LaneState.Normal, o));
        Assert.True(extra > o.SwitchCostPersonMinutes);

        var plan = LaneScheduler.Optimize(cp, night, LaneState.ReversibleInbound, AnyState, o);

        Assert.All(plan.States, s => Assert.Equal(LaneState.Normal, s));
    }

    [Fact]
    public void Without_an_operating_cost_a_harmless_layout_is_left_in_place()
    {
        // Documents why ActiveLayoutCostPerHour exists: with it at zero, a reversible lane left over
        // from the peak would stay out all night because switching back costs more than it saves.
        var plan = LaneScheduler.Optimize(ChokePoint(), Repeat(12, 700, 700, 300, 300), LaneState.ReversibleInbound, AnyState,
            Options(operatingCost: 0));

        Assert.All(plan.States, s => Assert.Equal(LaneState.ReversibleInbound, s));
    }

    [Fact]
    public void Empty_horizon_returns_an_empty_plan()
    {
        var plan = LaneScheduler.Optimize(ChokePoint(), [], LaneState.Normal, AnyState, Options());

        Assert.Empty(plan.States);
        Assert.Equal(0, plan.TotalCost);
    }

    /// <summary>
    /// Proof of optimality on small inputs: the DP result must equal the cheapest of every
    /// possible state sequence found by brute force.
    /// </summary>
    [Theory]
    [InlineData(1500, true)]
    [InlineData(300, true)]
    [InlineData(5000, false)]
    public void Dynamic_programming_matches_brute_force(double switchCost, bool reversible)
    {
        var cp = ChokePoint(reversible: reversible);
        var o = Options(switchCost);
        var slots = new List<SlotDemand>
        {
            Slot(0, 2700, 1300, 2500, 1000),
            Slot(1, 1700, 1500, 2200, 700),
            Slot(2, 1200, 2600, 900, 2400),
            Slot(3, 900, 900, 400, 400)
        };
        var initial = LaneState.Normal;

        var plan = LaneScheduler.Optimize(cp, slots, initial, AnyState, o);

        var states = Enum.GetValues<LaneState>().Where(s => TrafficMath.IsAllowed(cp, s)).ToArray();
        var best = double.PositiveInfinity;
        foreach (var sequence in Sequences(states, slots.Count))
        {
            var cost = 0.0;
            var previous = initial;
            for (var i = 0; i < sequence.Length; i++)
            {
                cost += LaneScheduler.SlotCost(cp, slots[i], sequence[i], o) + (sequence[i] == previous ? 0 : o.SwitchCostPersonMinutes);
                previous = sequence[i];
            }
            best = Math.Min(best, cost);
        }

        Assert.Equal(best, plan.TotalCost, precision: 6);
    }

    [Fact]
    public void Slot_cost_grows_with_demand()
    {
        var cp = ChokePoint();
        var o = Options();
        var low = LaneScheduler.SlotCost(cp, Slot(0, 1000, 1000, 500, 500), LaneState.Normal, o);
        var high = LaneScheduler.SlotCost(cp, Slot(0, 2500, 2500, 1500, 1500), LaneState.Normal, o);

        Assert.True(high > low);
    }

    private static IEnumerable<LaneState[]> Sequences(LaneState[] states, int length)
    {
        if (length == 0)
        {
            yield return [];
            yield break;
        }
        foreach (var head in states)
            foreach (var tail in Sequences(states, length - 1))
                yield return [head, .. tail];
    }
}

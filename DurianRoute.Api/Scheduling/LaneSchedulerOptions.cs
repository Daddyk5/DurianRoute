namespace DurianRoute.Api.Scheduling;

/// <summary>Tunable parameters of the lane-scheduling cost model (bound to "LaneScheduler" in appsettings).</summary>
public class LaneSchedulerOptions
{
    /// <summary>Cost of reconfiguring a choke point (cones, enforcers, transition), in person-minutes.</summary>
    public double SwitchCostPersonMinutes { get; set; } = 1500;

    /// <summary>Hourly cost of keeping a non-normal layout staffed (enforcers, cones), in person-minutes.</summary>
    public double ActiveLayoutCostPerHour { get; set; } = 250;

    /// <summary>Extra bus delay in mixed traffic per unit v/c, as a fraction of free-flow time (weaving, blocked stops).</summary>
    public double BusFrictionFactor { get; set; } = 0.6;

    /// <summary>Bus delay inside a dedicated bus lane, as a fraction of free-flow time.</summary>
    public double BusLaneDelayFactor { get; set; } = 0.1;

    /// <summary>How fast the live-vs-forecast correction fades per hour ahead (0–1).</summary>
    public double LiveCorrectionDecay { get; set; } = 0.5;

    /// <summary>Minutes between automatic re-plans.</summary>
    public int ReplanIntervalMinutes { get; set; } = 15;
}

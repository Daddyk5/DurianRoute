using DurianRoute.Api.Data;
using DurianRoute.Api.Scheduling;
using DurianRoute.Shared;

namespace DurianRoute.Tests;

internal static class TestData
{
    public static ChokePoint ChokePoint(int id = 1, int lanes = 3, bool reversible = true, double freeFlow = 4) => new()
    {
        Id = id,
        Name = $"CP{id}",
        Lat = 7.06,
        Lng = 125.60,
        LanesPerDirection = lanes,
        LaneCapacityVph = 900,
        FreeFlowMinutes = freeFlow,
        PeakLoadFactor = 1.0,
        BusPassengerRatio = 0.6,
        HasReversibleLane = reversible,
        ActiveLaneState = LaneState.Normal
    };

    public static LaneSchedulerOptions Options(double switchCost = 1500, double operatingCost = 250) => new()
    {
        SwitchCostPersonMinutes = switchCost,
        ActiveLayoutCostPerHour = operatingCost,
        BusFrictionFactor = 0.6,
        BusLaneDelayFactor = 0.1,
        LiveCorrectionDecay = 0.5
    };

    public static SlotDemand Slot(int hour, double inbound, double outbound, double busIn = 0, double busOut = 0) =>
        new(new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc).AddHours(hour), inbound, outbound, busIn, busOut);

    public static List<SlotDemand> Repeat(int count, double inbound, double outbound, double busIn = 0, double busOut = 0) =>
        Enumerable.Range(0, count).Select(h => Slot(h, inbound, outbound, busIn, busOut)).ToList();
}

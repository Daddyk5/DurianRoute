using DurianRoute.Api.Data;
using DurianRoute.Api.Simulation;
using DurianRoute.Api.Traffic;
using DurianRoute.Shared;

namespace DurianRoute.Tests;

public class FleetStateTests
{
    private static readonly DateTime Now = new(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc);

    private static (FleetState Fleet, LiveTrafficState Traffic) Create()
    {
        var route = new BusRoute
        {
            Id = 1,
            Code = "T1",
            Name = "Test",
            Stops =
            [
                new Stop { Id = 1, Name = "A", Lat = 7.000, Lng = 125.600, Sequence = 0 },
                new Stop { Id = 2, Name = "B", Lat = 7.010, Lng = 125.600, Sequence = 1 },
                new Stop { Id = 3, Name = "C", Lat = 7.020, Lng = 125.600, Sequence = 2 }
            ],
            Buses = [new Bus { Id = 10, PlateNumber = "T1-101", RouteId = 1 }]
        };
        var fleet = new FleetState();
        fleet.Initialize([route]);
        var traffic = new LiveTrafficState();
        traffic.Initialize([]);
        return (fleet, traffic);
    }

    [Fact]
    public void Buses_move_along_their_route()
    {
        var (fleet, traffic) = Create();
        var rng = new Random(1);

        var before = fleet.Tick(Now, 1, traffic, rng).Positions.Single();
        var after = fleet.Tick(Now.AddSeconds(30), 30, traffic, rng).Positions.Single();

        Assert.True(after.Lat > before.Lat, "Inbound bus should head north toward stop C");
        Assert.Equal(TravelDirection.Inbound, after.Direction);
    }

    [Fact]
    public void Held_bus_stops_and_falls_behind_schedule_then_resumes_after_release()
    {
        var (fleet, traffic) = Create();
        var rng = new Random(1);
        fleet.Tick(Now, 1, traffic, rng);

        Assert.True(fleet.Hold(10, 5, Now));
        var held1 = fleet.Tick(Now.AddSeconds(1), 60, traffic, rng).Positions.Single();
        var held2 = fleet.Tick(Now.AddSeconds(2), 60, traffic, rng).Positions.Single();

        Assert.Equal(BusStatus.Held, held2.Status);
        Assert.Equal(0, held2.SpeedKph);
        Assert.Equal(held1.Lat, held2.Lat);
        Assert.True(held2.DeviationMinutes > held1.DeviationMinutes);

        Assert.True(fleet.Release(10));
        var released = fleet.Tick(Now.AddSeconds(3), 5, traffic, rng).Positions.Single();
        Assert.NotEqual(BusStatus.Held, released.Status);
    }

    [Fact]
    public void Long_delay_raises_exactly_one_late_alert()
    {
        var (fleet, traffic) = Create();
        var rng = new Random(1);
        fleet.Hold(10, 30, Now);

        var events = Enumerable.Range(1, 10)
            .SelectMany(i => fleet.Tick(Now.AddSeconds(i), 60, traffic, rng).Events)
            .ToList();

        var alert = Assert.Single(events);
        Assert.True(alert.DeviationMinutes > 5);
        Assert.Contains("behind schedule", alert.Message);
    }

    [Fact]
    public void Commands_for_unknown_buses_fail()
    {
        var (fleet, _) = Create();

        Assert.False(fleet.Hold(999, 5, Now));
        Assert.False(fleet.Release(999));
    }

    [Fact]
    public void Bus_turns_around_after_reaching_the_terminal()
    {
        var (fleet, traffic) = Create();
        var rng = new Random(1);

        // ~2.2 km route at ≤ 33 km/h plus dwell and a 2-minute layover: 15 simulated minutes is plenty.
        var trace = Enumerable.Range(1, 90)
            .Select(i => fleet.Tick(Now.AddSeconds(i), 10, traffic, rng).Positions.Single())
            .ToList();

        var firstLayover = trace.FindIndex(p => p.Status == BusStatus.Layover);
        Assert.True(firstLayover >= 0, "Bus never reached the terminal");
        Assert.Equal(TravelDirection.Inbound, trace[firstLayover].Direction);
        Assert.Contains(trace.Skip(firstLayover), p => p.Direction == TravelDirection.Outbound && p.Status != BusStatus.Layover);
    }
}

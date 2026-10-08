using DurianRoute.Api.Data;
using DurianRoute.Api.Simulation;
using DurianRoute.Api.Traffic;
using DurianRoute.Api.Weather;
using DurianRoute.Shared;

namespace DurianRoute.Tests;

public class RoadGeometryTests
{
    private static readonly DateTime Now = new(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc);

    private static BusRoute Route(int buses) => new()
    {
        Id = 1,
        Code = "T1",
        Stops =
        [
            new Stop { Id = 1, Name = "A", Lat = 7.000, Lng = 125.600, Sequence = 0 },
            new Stop { Id = 2, Name = "B", Lat = 7.010, Lng = 125.610, Sequence = 1 }
        ],
        Buses = Enumerable.Range(1, buses).Select(i => new Bus { Id = i, PlateNumber = $"T1-10{i}", RouteId = 1 }).ToList()
    };

    [Fact]
    public void Buses_follow_the_road_geometry_instead_of_a_straight_line()
    {
        // An L-shaped road: north first, then east. A straight line would move diagonally.
        var road = new List<LatLng> { new(7.000, 125.600), new(7.010, 125.600), new(7.010, 125.610) };
        var fleet = new FleetState();
        fleet.Initialize([Route(1)], new Dictionary<int, List<LatLng>> { [1] = road });
        var traffic = new LiveTrafficState();
        traffic.Initialize([]);
        var rng = new Random(3);

        var positions = Enumerable.Range(1, 12)
            .Select(i => fleet.Tick(Now.AddSeconds(i), 10, traffic, rng).Positions.Single())
            .ToList();

        // While still on the first leg the bus moves north only, staying on longitude 125.600.
        var firstLeg = positions.Where(p => p.Lat < 7.0095).ToList();
        Assert.NotEmpty(firstLeg);
        Assert.All(firstLeg, p => Assert.Equal(125.600, p.Lng, precision: 6));
        Assert.All(firstLeg, p => Assert.InRange(p.HeadingDegrees, 0, 1));
    }

    [Fact]
    public void Missing_geometry_falls_back_to_straight_lines_between_stops()
    {
        var fleet = new FleetState();
        fleet.Initialize([Route(1)]);
        var traffic = new LiveTrafficState();
        traffic.Initialize([]);

        var p = fleet.Tick(Now.AddSeconds(1), 30, traffic, new Random(1)).Positions.Single();

        // On the diagonal A→B, latitude and longitude advance together.
        Assert.Equal(p.Lat - 7.000, p.Lng - 125.600, precision: 4);
    }

    [Fact]
    public void Headway_is_reported_for_the_following_bus_only()
    {
        // Buses 1 and 3 run inbound (bus 3 further ahead); bus 2 runs outbound alone.
        var fleet = new FleetState();
        fleet.Initialize([Route(3)]);
        var traffic = new LiveTrafficState();
        traffic.Initialize([]);

        var positions = fleet.Tick(Now.AddSeconds(1), 1, traffic, new Random(1)).Positions.ToDictionary(p => p.BusId);

        Assert.Null(positions[3].HeadwayMinutes);           // lead inbound bus
        Assert.NotNull(positions[1].HeadwayMinutes);        // follows bus 3
        Assert.True(positions[1].HeadwayMinutes > 0);
        Assert.Null(positions[2].HeadwayMinutes);           // only outbound bus
    }
}

public class WeatherImpactTests
{
    [Theory]
    [InlineData(0.0, 3, 1.0)]       // overcast, dry
    [InlineData(0.4, 61, 0.92)]     // light rain
    [InlineData(4.0, 63, 0.85)]     // moderate rain
    [InlineData(9.0, 65, 0.75)]     // heavy rain
    [InlineData(0.0, 95, 0.75)]     // thunderstorm without measured rain yet
    public void Rain_reduces_road_capacity(double mm, int code, double expected) =>
        Assert.Equal(expected, WeatherService.CapacityFactor(mm, code));

    [Fact]
    public void Weather_lowers_capacity_at_choke_points_and_raises_v_over_c()
    {
        var cp = TestData.ChokePoint();
        var dry = new LiveTrafficState();
        dry.Initialize([TestData.ChokePoint()]);
        var wet = new LiveTrafficState();
        wet.Initialize([cp]);
        var now = new DateTime(2026, 10, 7, 23, 0, 0, DateTimeKind.Utc);   // 07:00 PH, inbound peak

        var dryStatus = dry.Update(now, new Random(1)).Single();
        var wetStatus = wet.Update(now, new Random(1), weatherFactor: 0.75).Single();

        Assert.Equal(0.75, wetStatus.CapacityFactor);
        Assert.True(wetStatus.InboundVc > dryStatus.InboundVc);
        Assert.Equal(0.75, wet.CapacityFactor(cp.Id, now));
    }

    [Theory]
    [InlineData(0, "Clear")]
    [InlineData(3, "Overcast")]
    [InlineData(63, "Rain")]
    [InlineData(95, "Thunderstorm")]
    public void Describes_wmo_weather_codes(int code, string expected) =>
        Assert.Equal(expected, WeatherService.Describe(code));
}

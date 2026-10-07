using DurianRoute.Api.Traffic;
using DurianRoute.Shared;
using static DurianRoute.Tests.TestData;

namespace DurianRoute.Tests;

public class TrafficMathTests
{
    [Fact]
    public void Bpr_delay_is_zero_without_traffic_and_fifteen_percent_at_capacity()
    {
        Assert.Equal(0, TrafficMath.BprDelay(4, 0));
        Assert.Equal(0.6, TrafficMath.BprDelay(4, 1.0), precision: 10);   // 4 min · 0.15 · 1⁴
    }

    [Fact]
    public void Bpr_delay_is_capped_when_oversaturated()
    {
        Assert.Equal(TrafficMath.BprDelay(4, TrafficMath.MaxVc), TrafficMath.BprDelay(4, 10));
    }

    [Theory]
    [InlineData(LaneState.Normal, 3, 3, false, false)]
    [InlineData(LaneState.ReversibleInbound, 4, 2, false, false)]
    [InlineData(LaneState.ReversibleOutbound, 2, 4, false, false)]
    [InlineData(LaneState.BusLaneInbound, 2, 3, true, false)]
    [InlineData(LaneState.BusLaneOutbound, 3, 2, false, true)]
    public void Lane_allocation_for_each_state(LaneState state, int carIn, int carOut, bool busIn, bool busOut)
    {
        var a = TrafficMath.Allocate(ChokePoint(lanes: 3), state);

        Assert.Equal(new LaneAllocation(carIn, carOut, busIn, busOut), a);
    }

    [Fact]
    public void Reversible_lanes_require_the_hardware()
    {
        Assert.False(TrafficMath.IsAllowed(ChokePoint(reversible: false), LaneState.ReversibleInbound));
        Assert.True(TrafficMath.IsAllowed(ChokePoint(reversible: false), LaneState.BusLaneInbound));
        Assert.False(TrafficMath.IsAllowed(ChokePoint(lanes: 1), LaneState.BusLaneInbound));
    }

    [Fact]
    public void Volume_to_capacity_accounts_for_incidents()
    {
        Assert.Equal(1.0, TrafficMath.VolumeToCapacity(1800, 2, 900), precision: 10);
        Assert.Equal(1.0, TrafficMath.VolumeToCapacity(1080, 2, 900, capacityFactor: 0.6), precision: 10);
    }
}

public class DavaoCalendarTests
{
    [Fact]
    public void Converts_utc_to_philippine_time()
    {
        var local = DavaoCalendar.ToLocal(new DateTime(2026, 10, 7, 22, 0, 0, DateTimeKind.Utc));
        Assert.Equal(new DateTime(2026, 10, 8, 6, 0, 0), local);
    }

    [Theory]
    [InlineData(2026, 10, 15, true)]
    [InlineData(2026, 10, 31, true)]
    [InlineData(2026, 2, 28, true)]
    [InlineData(2026, 10, 14, false)]
    public void Payday_is_the_15th_and_the_last_day(int y, int m, int d, bool expected) =>
        Assert.Equal(expected, DavaoCalendar.IsPayday(new DateTime(y, m, d)));

    [Theory]
    [InlineData(2026, 12, 25, true)]
    [InlineData(2026, 8, 18, true)]    // Kadayawan week
    [InlineData(2026, 10, 7, false)]
    public void Recognises_holidays(int y, int m, int d, bool expected) =>
        Assert.Equal(expected, DavaoCalendar.IsHoliday(new DateTime(y, m, d)));

    [Theory]
    [InlineData(2026, 10, 7, true)]     // Wednesday
    [InlineData(2026, 10, 10, false)]   // Saturday
    [InlineData(2026, 4, 15, false)]    // summer break
    public void School_days(int y, int m, int d, bool expected) =>
        Assert.Equal(expected, DavaoCalendar.IsSchoolDay(new DateTime(y, m, d)));
}

public class SyntheticTrafficTests
{
    private static DateTime Utc(int year, int month, int day, int localHour) =>
        DavaoCalendar.ToUtc(new DateTime(year, month, day, localHour, 0, 0));

    [Fact]
    public void Same_inputs_always_give_the_same_traffic()
    {
        var cp = ChokePoint();
        var hour = Utc(2026, 10, 7, 8);

        Assert.Equal(SyntheticTraffic.Sample(cp, hour, TravelDirection.Inbound), SyntheticTraffic.Sample(cp, hour, TravelDirection.Inbound));
    }

    [Fact]
    public void Morning_peak_is_inbound_and_evening_peak_is_outbound()
    {
        var cp = ChokePoint();
        var am = Utc(2026, 10, 7, 7);
        var pm = Utc(2026, 10, 7, 17);

        Assert.True(SyntheticTraffic.Sample(cp, am, TravelDirection.Inbound).VehicleVolume
                  > 1.4 * SyntheticTraffic.Sample(cp, am, TravelDirection.Outbound).VehicleVolume);
        Assert.True(SyntheticTraffic.Sample(cp, pm, TravelDirection.Outbound).VehicleVolume
                  > 1.4 * SyntheticTraffic.Sample(cp, pm, TravelDirection.Inbound).VehicleVolume);
    }

    [Fact]
    public void Sundays_are_lighter_than_weekdays()
    {
        var cp = ChokePoint();
        double DayTotal(int day) => Enumerable.Range(6, 15)
            .Sum(h => SyntheticTraffic.Sample(cp, Utc(2026, 10, day, h), TravelDirection.Inbound).VehicleVolume);

        Assert.True(DayTotal(11) < 0.8 * DayTotal(7));   // Sunday vs Wednesday
    }

    [Fact]
    public void Night_traffic_is_far_below_peak()
    {
        var cp = ChokePoint();
        var night = SyntheticTraffic.Sample(cp, Utc(2026, 10, 7, 3), TravelDirection.Inbound).VehicleVolume;
        var peak = SyntheticTraffic.Sample(cp, Utc(2026, 10, 7, 7), TravelDirection.Inbound).VehicleVolume;

        Assert.True(night < peak / 4);
    }
}

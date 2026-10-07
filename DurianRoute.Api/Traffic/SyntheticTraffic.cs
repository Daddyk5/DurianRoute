using DurianRoute.Api.Data;
using DurianRoute.Shared;

namespace DurianRoute.Api.Traffic;

public readonly record struct TrafficSample(double VehicleVolume, double BusPassengers, double RainMm, bool IsHoliday);

/// <summary>
/// Deterministic "ground truth" traffic for the simulation. The same (choke point, hour, direction)
/// always yields the same value, so the backfilled history, the live feed and forecast-vs-actual
/// comparisons all agree. Demand follows Davao commuter patterns: inbound AM peak, outbound PM peak,
/// lighter weekends/holidays, payday surges and afternoon rain.
/// </summary>
public static class SyntheticTraffic
{
    public static TrafficSample Sample(ChokePoint cp, DateTime hourStartUtc, TravelDirection direction)
    {
        var local = DavaoCalendar.ToLocal(hourStartUtc);
        var h = local.Hour + 0.5;
        var rng = new Random(Seed(cp.Id, hourStartUtc, direction));

        var peakAm = Gaussian(h, 7.5, 1.1);
        var peakPm = Gaussian(h, 17.75, 1.3);
        var plateau = 0.36 * Sigmoid((h - 5.5) * 2) * Sigmoid((21 - h) * 1.5);
        var profile = direction == TravelDirection.Inbound
            ? 0.08 + plateau + 0.70 * peakAm + 0.15 * peakPm
            : 0.08 + plateau + 0.15 * peakAm + 0.70 * peakPm;
        profile /= 1.14;

        var isHoliday = DavaoCalendar.IsHoliday(local);
        var day = local.DayOfWeek switch
        {
            DayOfWeek.Saturday => 0.80,
            DayOfWeek.Sunday => 0.65,
            _ => 1.0
        };
        if (isHoliday) day *= DavaoCalendar.IsKadayawan(local) ? 0.85 : 0.60;
        if (DavaoCalendar.IsPayday(local)) day *= 1.08;
        if (DavaoCalendar.IsSchoolDay(local)) day *= 1 + 0.08 * peakAm;

        var rainMm = RainFor(local);
        var rainFactor = rainMm > 0 ? 1.05 : 1.0;

        var capacity = cp.LanesPerDirection * cp.LaneCapacityVph;
        var volume = capacity * cp.PeakLoadFactor * profile * day * rainFactor * Noise(rng, 0.06);

        var weekendBus = local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 0.8 : 1.0;
        var busPax = volume * cp.BusPassengerRatio * (1 + 0.4 * (peakAm + peakPm)) * weekendBus * Noise(rng, 0.08);

        return new TrafficSample(Math.Round(volume, 1), Math.Round(busPax, 1), rainMm, isHoliday);
    }

    /// <summary>Roughly 45% of days get an afternoon downpour (13:00–18:00 local).</summary>
    private static double RainFor(DateTime local)
    {
        if (local.Hour is < 13 or > 18) return 0;
        var dayRng = new Random(local.Year * 1000 + local.DayOfYear);
        return dayRng.NextDouble() < 0.45 ? Math.Round(2 + dayRng.NextDouble() * 18, 1) : 0;
    }

    private static int Seed(int cpId, DateTime hourUtc, TravelDirection d)
    {
        var hourIndex = hourUtc.Ticks / TimeSpan.TicksPerHour;
        unchecked
        {
            var x = (ulong)hourIndex * 2654435761UL ^ (ulong)cpId * 40503UL ^ (ulong)d * 7919UL;
            return (int)(x ^ (x >> 32));
        }
    }

    private static double Gaussian(double x, double mean, double sd) => Math.Exp(-Math.Pow(x - mean, 2) / (2 * sd * sd));

    private static double Sigmoid(double x) => 1 / (1 + Math.Exp(-x));

    private static double Noise(Random rng, double sd)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = rng.NextDouble();
        var z = Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        return Math.Max(0.5, 1 + sd * z);
    }
}

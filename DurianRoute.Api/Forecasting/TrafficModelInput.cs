using DurianRoute.Api.Traffic;
using DurianRoute.Shared;
using Microsoft.ML.Data;

namespace DurianRoute.Api.Forecasting;

/// <summary>Feature row for the hourly traffic-volume regression.</summary>
public class TrafficModelInput
{
    public string ChokePoint { get; set; } = "";
    public float Direction { get; set; }
    public float Hour { get; set; }
    public float DayOfWeek { get; set; }
    public float IsWeekend { get; set; }
    public float IsHoliday { get; set; }
    public float IsSchoolDay { get; set; }
    public float IsPayday { get; set; }

    /// <summary>Volume at the same hour yesterday.</summary>
    public float Lag24 { get; set; }

    /// <summary>Volume at the same hour last week.</summary>
    public float Lag168 { get; set; }

    [ColumnName("Label")]
    public float VehicleVolume { get; set; }

    public static readonly string[] FeatureColumns =
        [nameof(Direction), nameof(Hour), nameof(DayOfWeek), nameof(IsWeekend), nameof(IsHoliday),
         nameof(IsSchoolDay), nameof(IsPayday), nameof(Lag24), nameof(Lag168)];

    public static TrafficModelInput Create(int chokePointId, DateTime hourStartUtc, TravelDirection direction,
        double lag24, double lag168, double label = 0)
    {
        var local = DavaoCalendar.ToLocal(hourStartUtc);
        return new TrafficModelInput
        {
            ChokePoint = $"CP{chokePointId}",
            Direction = (float)direction,
            Hour = local.Hour,
            DayOfWeek = (float)local.DayOfWeek,
            IsWeekend = local.DayOfWeek is System.DayOfWeek.Saturday or System.DayOfWeek.Sunday ? 1 : 0,
            IsHoliday = DavaoCalendar.IsHoliday(local) ? 1 : 0,
            IsSchoolDay = DavaoCalendar.IsSchoolDay(local) ? 1 : 0,
            IsPayday = DavaoCalendar.IsPayday(local) ? 1 : 0,
            Lag24 = (float)lag24,
            Lag168 = (float)lag168,
            VehicleVolume = (float)label
        };
    }
}

public class TrafficModelOutput
{
    [ColumnName("Score")]
    public float PredictedVolume { get; set; }
}

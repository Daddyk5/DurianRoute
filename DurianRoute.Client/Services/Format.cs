using DurianRoute.Shared;
using MudBlazor;

namespace DurianRoute.Client.Services;

/// <summary>Display helpers. Times are shown in Philippine time (UTC+8) regardless of browser settings.</summary>
public static class Format
{
    public static DateTime Ph(DateTime utc) => utc.ToUniversalTime().AddHours(8);

    public static string Time(DateTime utc) => Ph(utc).ToString("HH:mm");

    public static string DateTimeText(DateTime utc) => Ph(utc).ToString("MMM d, HH:mm");

    public static string Window(DateTime startUtc, DateTime endUtc) =>
        Ph(startUtc).Date == Ph(endUtc).Date || Ph(endUtc).TimeOfDay == TimeSpan.Zero
            ? $"{Ph(startUtc):MMM d, HH:mm}–{Ph(endUtc):HH:mm}"
            : $"{Ph(startUtc):MMM d, HH:mm} – {Ph(endUtc):MMM d, HH:mm}";

    public static string Deviation(double minutes) =>
        Math.Abs(minutes) < 0.5 ? "on time" : minutes > 0 ? $"+{minutes:0.0} min late" : $"{-minutes:0.0} min early";

    public static Color DeviationColor(double minutes) =>
        minutes > 5 ? Color.Error : minutes > 2 ? Color.Warning : minutes < -4 ? Color.Info : Color.Success;

    public static Color CongestionColor(double vc) =>
        vc >= 1.0 ? Color.Error : vc >= 0.85 ? Color.Warning : vc >= 0.65 ? Color.Secondary : Color.Success;

    public static string Lane(LaneState state) => LaneStateText.Describe(state);

    public static string Status(BusStatus s) => s switch
    {
        BusStatus.InService => "Moving",
        BusStatus.AtStop => "At stop",
        BusStatus.Layover => "Layover",
        BusStatus.Held => "Held by dispatcher",
        _ => s.ToString()
    };

    public static Color StatusColor(RecommendationStatus status) => status switch
    {
        RecommendationStatus.Pending => Color.Warning,
        RecommendationStatus.Approved => Color.Info,
        RecommendationStatus.Active => Color.Success,
        RecommendationStatus.Rejected => Color.Error,
        _ => Color.Default
    };
}

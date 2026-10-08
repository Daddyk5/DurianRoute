using System.Globalization;
using System.Text.Json;
using DurianRoute.Api.Hubs;
using DurianRoute.Api.Traffic;
using DurianRoute.Shared;
using Microsoft.AspNetCore.SignalR;

namespace DurianRoute.Api.Weather;

/// <summary>
/// Latest Davao City weather from Open-Meteo (free, no API key). Rain lowers road capacity, which the
/// live traffic simulation and the lane planner both use. Thread-safe singleton.
/// </summary>
public class WeatherService
{
    private readonly Lock _gate = new();
    private WeatherDto? _current;

    public WeatherDto? Current
    {
        get { lock (_gate) return _current; }
    }

    public void Set(WeatherDto weather)
    {
        lock (_gate) _current = weather;
    }

    /// <summary>Road capacity multiplier right now (1.0 when dry or unknown).</summary>
    public double CapacityFactorNow() => Current?.RoadCapacityFactor ?? 1.0;

    /// <summary>Road capacity multiplier expected for an upcoming hour, from the hourly forecast.</summary>
    public double CapacityFactorAt(DateTime hourStartUtc)
    {
        var hour = Current?.NextHours.FirstOrDefault(h => h.HourStartUtc == hourStartUtc);
        return hour is null ? 1.0 : CapacityFactor(hour.PrecipitationMm, hour.WeatherCode);
    }

    /// <summary>
    /// Wet-road capacity loss. Light rain ≈ −8%, moderate ≈ −15%, heavy rain or thunderstorm ≈ −25%,
    /// in line with commonly cited saturation-flow reductions for rain.
    /// </summary>
    public static double CapacityFactor(double precipitationMm, int weatherCode)
    {
        if (weatherCode is >= 95 and <= 99 || precipitationMm >= 7.5) return 0.75;
        if (precipitationMm >= 2.5) return 0.85;
        if (precipitationMm >= 0.1 || weatherCode is >= 51 and <= 67 or >= 80 and <= 82) return 0.92;
        return 1.0;
    }

    public static string Impact(double factor) => factor switch
    {
        >= 1.0 => "Dry roads, normal capacity",
        >= 0.9 => "Wet roads, about 8% less capacity",
        >= 0.8 => "Moderate rain, about 15% less capacity",
        _ => "Heavy rain, about 25% less capacity"
    };

    /// <summary>WMO weather interpretation codes used by Open-Meteo.</summary>
    public static string Describe(int code) => code switch
    {
        0 => "Clear",
        1 => "Mostly clear",
        2 => "Partly cloudy",
        3 => "Overcast",
        45 or 48 => "Fog",
        51 or 53 or 55 => "Drizzle",
        56 or 57 => "Freezing drizzle",
        61 => "Light rain",
        63 => "Rain",
        65 => "Heavy rain",
        66 or 67 => "Freezing rain",
        71 or 73 or 75 or 77 => "Snow",
        80 => "Light showers",
        81 => "Showers",
        82 => "Violent showers",
        85 or 86 => "Snow showers",
        95 => "Thunderstorm",
        96 or 99 => "Thunderstorm with hail",
        _ => "Unknown"
    };
}

/// <summary>Polls Open-Meteo every 10 minutes and pushes changes to every dashboard.</summary>
public class WeatherWorker(
    WeatherService weather,
    IHttpClientFactory httpFactory,
    IHubContext<TelemetryHub> hub,
    IConfiguration config,
    ILogger<WeatherWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                var latest = await FetchAsync(stoppingToken);
                weather.Set(latest);
                await hub.Clients.All.SendAsync(HubEvents.Weather, latest, stoppingToken);
                logger.LogInformation("Weather: {Condition}, {Temp}°C, {Rain} mm ({Impact})",
                    latest.Condition, latest.TemperatureC, latest.PrecipitationMm, latest.RoadImpact);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Weather update failed: {Message}", ex.Message);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task<WeatherDto> FetchAsync(CancellationToken ct)
    {
        var lat = config.GetValue("Weather:Latitude", 7.0731).ToString(CultureInfo.InvariantCulture);
        var lng = config.GetValue("Weather:Longitude", 125.6128).ToString(CultureInfo.InvariantCulture);
        var url = "https://api.open-meteo.com/v1/forecast" +
                  $"?latitude={lat}&longitude={lng}" +
                  "&current=temperature_2m,relative_humidity_2m,apparent_temperature,precipitation,weather_code,wind_speed_10m,is_day" +
                  "&hourly=temperature_2m,precipitation_probability,precipitation,weather_code" +
                  "&forecast_hours=24&timezone=GMT";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var doc = JsonDocument.Parse(await httpFactory.CreateClient("weather").GetStringAsync(url, timeout.Token));
        var current = doc.RootElement.GetProperty("current");
        var hourly = doc.RootElement.GetProperty("hourly");

        var times = hourly.GetProperty("time").EnumerateArray().Select(t => ParseUtc(t.GetString()!)).ToList();
        var temps = hourly.GetProperty("temperature_2m").EnumerateArray().Select(Num).ToList();
        var probs = hourly.GetProperty("precipitation_probability").EnumerateArray().Select(e => (int)Num(e)).ToList();
        var rain = hourly.GetProperty("precipitation").EnumerateArray().Select(Num).ToList();
        var codes = hourly.GetProperty("weather_code").EnumerateArray().Select(e => (int)Num(e)).ToList();

        var hours = times.Select((t, i) => new WeatherHourDto(t, temps[i], probs[i], rain[i], codes[i], WeatherService.Describe(codes[i])))
            .Where(h => h.HourStartUtc >= DavaoCalendar.CurrentHourUtc())
            .Take(24)
            .ToList();

        var code = (int)Num(current.GetProperty("weather_code"));
        var precipitation = Num(current.GetProperty("precipitation"));
        var factor = WeatherService.CapacityFactor(precipitation, code);

        return new WeatherDto(
            IsLive: true,
            ObservedAtUtc: ParseUtc(current.GetProperty("time").GetString()!),
            TemperatureC: Math.Round(Num(current.GetProperty("temperature_2m")), 1),
            FeelsLikeC: Math.Round(Num(current.GetProperty("apparent_temperature")), 1),
            HumidityPercent: (int)Num(current.GetProperty("relative_humidity_2m")),
            PrecipitationMm: precipitation,
            WindKph: Math.Round(Num(current.GetProperty("wind_speed_10m")), 1),
            WeatherCode: code,
            Condition: WeatherService.Describe(code),
            IsDay: Num(current.GetProperty("is_day")) > 0,
            RoadCapacityFactor: factor,
            RoadImpact: WeatherService.Impact(factor),
            NextHours: hours);
    }

    private static double Num(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetDouble() : 0;

    private static DateTime ParseUtc(string s) =>
        DateTime.SpecifyKind(DateTime.Parse(s, CultureInfo.InvariantCulture), DateTimeKind.Utc);
}

using System.Text.Json;
using DurianRoute.Api.Data;
using DurianRoute.Shared;

namespace DurianRoute.Api.Simulation;

/// <summary>
/// Road-following geometry for each bus route. Shapes come from the public OSRM router (OpenStreetMap
/// data) once, are cached in App_Data/route-shapes.json, and fall back to straight lines between stops
/// when the router cannot be reached.
/// </summary>
public class RouteShapeService(IHttpClientFactory httpFactory, IWebHostEnvironment env, IConfiguration config, ILogger<RouteShapeService> logger)
{
    private readonly Dictionary<int, List<LatLng>> _shapes = [];

    private string CachePath => Path.Combine(env.ContentRootPath, "App_Data", "route-shapes.json");

    public IReadOnlyDictionary<int, List<LatLng>> Shapes => _shapes;

    public List<LatLng> PathFor(BusRoute route) =>
        _shapes.TryGetValue(route.Id, out var path) ? path : StraightLine(route);

    public async Task LoadAsync(IEnumerable<BusRoute> routes, CancellationToken ct)
    {
        var cache = await ReadCacheAsync(ct);
        var changed = false;

        foreach (var route in routes)
        {
            var stops = route.Stops.OrderBy(s => s.Sequence).ToList();
            var key = CacheKey(route.Code, stops);
            if (cache.TryGetValue(key, out var cached) && cached.Count >= 2)
            {
                _shapes[route.Id] = cached;
                continue;
            }

            var fetched = await FetchAsync(stops, ct);
            if (fetched is null) continue;   // straight-line fallback via PathFor
            _shapes[route.Id] = cached = fetched;
            cache[key] = cached;
            changed = true;
            logger.LogInformation("Snapped route {Route} to roads ({Points} points)", route.Code, fetched.Count);
        }

        if (changed) await WriteCacheAsync(cache, ct);
    }

    private async Task<List<LatLng>?> FetchAsync(List<Stop> stops, CancellationToken ct)
    {
        var baseUrl = config["Routing:OsrmBaseUrl"] ?? "https://router.project-osrm.org";
        var coords = string.Join(';', stops.Select(s => FormattableString.Invariant($"{s.Lng},{s.Lat}")));
        var url = $"{baseUrl.TrimEnd('/')}/route/v1/driving/{coords}?overview=full&geometries=geojson";

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var doc = JsonDocument.Parse(await httpFactory.CreateClient("routing").GetStringAsync(url, timeout.Token));
            if (doc.RootElement.GetProperty("code").GetString() != "Ok") return null;

            return doc.RootElement.GetProperty("routes")[0].GetProperty("geometry").GetProperty("coordinates")
                .EnumerateArray()
                .Select(p => new LatLng(Math.Round(p[1].GetDouble(), 6), Math.Round(p[0].GetDouble(), 6)))
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            logger.LogWarning("Road routing unavailable ({Message}); using straight lines between stops", ex.Message);
            return null;
        }
    }

    private static List<LatLng> StraightLine(BusRoute route) =>
        route.Stops.OrderBy(s => s.Sequence).Select(s => new LatLng(s.Lat, s.Lng)).ToList();

    /// <summary>The key changes whenever a route's stops move, which forces a fresh fetch.</summary>
    private static string CacheKey(string code, List<Stop> stops) =>
        code + ":" + string.Join('|', stops.Select(s => FormattableString.Invariant($"{s.Lat:F5},{s.Lng:F5}")));

    private async Task<Dictionary<string, List<LatLng>>> ReadCacheAsync(CancellationToken ct)
    {
        try
        {
            if (!File.Exists(CachePath)) return [];
            await using var stream = File.OpenRead(CachePath);
            return await JsonSerializer.DeserializeAsync<Dictionary<string, List<LatLng>>>(stream, cancellationToken: ct) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task WriteCacheAsync(Dictionary<string, List<LatLng>> cache, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
        await using var stream = File.Create(CachePath);
        await JsonSerializer.SerializeAsync(stream, cache, cancellationToken: ct);
    }
}

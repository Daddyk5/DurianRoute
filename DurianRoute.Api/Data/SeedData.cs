using DurianRoute.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace DurianRoute.Api.Data;

/// <summary>
/// Initial Davao City network. Coordinates are approximate and routes are straight lines
/// between stops; replace them with surveyed data / CTTMO figures for production use.
/// </summary>
public static class SeedData
{
    public static async Task EnsureSeededAsync(DurianDbContext db, IConfiguration config, CancellationToken ct = default)
    {
        if (!await db.Routes.AnyAsync(ct))
        {
            db.Routes.AddRange(BuildRoutes());
            db.ChokePoints.AddRange(BuildChokePoints());
            await db.SaveChangesAsync(ct);
        }

        // Keep route colors in sync with the palette (they must stay distinct from congestion colors).
        var palette = BuildRoutes().ToDictionary(r => r.Code, r => r.Color);
        var routesToRecolor = await db.Routes.ToListAsync(ct);
        foreach (var r in routesToRecolor.Where(r => palette.TryGetValue(r.Code, out var color) && r.Color != color))
            r.Color = palette[r.Code];
        await db.SaveChangesAsync(ct);

        if (!await db.Users.AnyAsync(ct))
        {
            var hasher = new PasswordHasher<AppUser>();
            foreach (var section in config.GetSection("SeedUsers").GetChildren())
            {
                var user = new AppUser
                {
                    UserName = section["UserName"]!,
                    Role = section["Role"] ?? Roles.Dispatcher
                };
                user.PasswordHash = hasher.HashPassword(user, section["Password"]!);
                db.Users.Add(user);
            }
            await db.SaveChangesAsync(ct);
        }
    }

    private static IEnumerable<BusRoute> BuildRoutes()
    {
        yield return Route("T1", "Toril – Ulas – Bankerohan – San Pedro", "#4F46E5",
            ("Toril Public Market", 7.0186, 125.4986),
            ("Crossing Bayabas", 7.0290, 125.5180),
            ("Ulas Junction", 7.0410, 125.5530),
            ("Matina Crossing", 7.0570, 125.5830),
            ("Ecoland (Quimpo Blvd)", 7.0610, 125.5960),
            ("Bankerohan Public Market", 7.0663, 125.6012),
            ("San Pedro / Rizal Park", 7.0731, 125.6128));

        yield return Route("P2", "Panacan – Sasa – Lanang – San Pedro", "#0891B2",
            ("Panacan Terminal", 7.1480, 125.6600),
            ("Sasa Wharf", 7.1265, 125.6590),
            ("J.P. Laurel – Lanang", 7.0985, 125.6310),
            ("Agdao Public Market", 7.0855, 125.6230),
            ("Magsaysay Avenue", 7.0760, 125.6200),
            ("San Pedro / Rizal Park", 7.0731, 125.6128));

        yield return Route("B3", "Buhangin – Bajada – Roxas", "#C026D3",
            ("Buhangin Crossing", 7.1110, 125.6140),
            ("Cabaguio Avenue", 7.0980, 125.6170),
            ("Bajada (J.P. Laurel)", 7.0875, 125.6135),
            ("Agdao Junction", 7.0830, 125.6150),
            ("Roxas Avenue", 7.0718, 125.6108));

        yield return Route("M4", "Mintal – Ma-a – Bankerohan", "#78350F",
            ("Mintal", 7.0905, 125.5000),
            ("Catalunan Grande", 7.0760, 125.5450),
            ("Ma-a Diversion", 7.0830, 125.5830),
            ("Matina Pangi", 7.0700, 125.5960),
            ("Bankerohan Public Market", 7.0663, 125.6012));
    }

    private static BusRoute Route(string code, string name, string color, params (string Name, double Lat, double Lng)[] stops)
    {
        var route = new BusRoute { Code = code, Name = name, Color = color };
        route.Stops = stops.Select((s, i) => new Stop { Name = s.Name, Lat = s.Lat, Lng = s.Lng, Sequence = i }).ToList();
        route.Buses = Enumerable.Range(1, 4)
            .Select(i => new Bus { PlateNumber = $"{code}-{100 + i}", Capacity = 60 })
            .ToList();
        return route;
    }

    private static IEnumerable<ChokePoint> BuildChokePoints() =>
    [
        Cp("Bankerohan Bridge", 7.0663, 125.6012, lanes: 2, peak: 1.15, busRatio: 0.9, reversible: false, freeFlow: 5),
        Cp("Matina Crossing", 7.0570, 125.5830, lanes: 3, peak: 1.05, busRatio: 0.7, reversible: true, freeFlow: 4),
        Cp("Ulas Junction", 7.0410, 125.5530, lanes: 2, peak: 1.00, busRatio: 0.6, reversible: true, freeFlow: 4),
        Cp("J.P. Laurel – Lanang", 7.0985, 125.6310, lanes: 3, peak: 1.00, busRatio: 0.5, reversible: true, freeFlow: 5),
        Cp("Buhangin Crossing", 7.1110, 125.6140, lanes: 2, peak: 1.05, busRatio: 0.6, reversible: false, freeFlow: 3),
        Cp("Panacan", 7.1480, 125.6600, lanes: 2, peak: 0.95, busRatio: 0.55, reversible: true, freeFlow: 4),
        Cp("Agdao Public Market", 7.0855, 125.6230, lanes: 2, peak: 1.00, busRatio: 0.8, reversible: false, freeFlow: 3),
        Cp("Ma-a Diversion", 7.0830, 125.5830, lanes: 2, peak: 0.90, busRatio: 0.4, reversible: true, freeFlow: 4),
    ];

    private static ChokePoint Cp(string name, double lat, double lng, int lanes, double peak, double busRatio, bool reversible, double freeFlow) => new()
    {
        Name = name,
        Lat = lat,
        Lng = lng,
        LanesPerDirection = lanes,
        LaneCapacityVph = 900,
        FreeFlowMinutes = freeFlow,
        PeakLoadFactor = peak,
        BusPassengerRatio = busRatio,
        HasReversibleLane = reversible
    };
}

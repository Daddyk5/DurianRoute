namespace DurianRoute.Api.Traffic;

public static class Geo
{
    private const double EarthRadiusMeters = 6_371_000;

    public static double DistanceMeters(double lat1, double lng1, double lat2, double lng2)
    {
        var dLat = ToRad(lat2 - lat1);
        var dLng = ToRad(lng2 - lng1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
              + Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2)) * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return 2 * EarthRadiusMeters * Math.Asin(Math.Sqrt(a));
    }

    public static double BearingDegrees(double lat1, double lng1, double lat2, double lng2)
    {
        var y = Math.Sin(ToRad(lng2 - lng1)) * Math.Cos(ToRad(lat2));
        var x = Math.Cos(ToRad(lat1)) * Math.Sin(ToRad(lat2))
              - Math.Sin(ToRad(lat1)) * Math.Cos(ToRad(lat2)) * Math.Cos(ToRad(lng2 - lng1));
        return (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
    }

    private static double ToRad(double deg) => deg * Math.PI / 180;
}

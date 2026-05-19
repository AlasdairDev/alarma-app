namespace AlarmaApp.Services;

public class OpenStreetMapTileService
{
    private const int MinZoom = 0;
    private const int MaxZoom = 19;
    private const double MaxLatitude = 85.05112878;

    public Uri GetTileUri(double latitude, double longitude, int zoom)
    {
        var safeLatitude = ClampLatitude(latitude);
        var safeLongitude = ClampLongitude(longitude);
        var safeZoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        var (x, y) = GetTileCoordinates(safeLatitude, safeLongitude, safeZoom);
        return new Uri($"https://tile.openstreetmap.org/{safeZoom}/{x}/{y}.png");
    }

    private static (int X, int Y) GetTileCoordinates(double latitude, double longitude, int zoom)
    {
        var latRad = latitude * Math.PI / 180.0;
        var n = Math.Pow(2.0, zoom);
        var xTile = (int)Math.Floor((longitude + 180.0) / 360.0 * n);
        var yTile = (int)Math.Floor((1.0 - Math.Log(Math.Tan(latRad) + 1.0 / Math.Cos(latRad)) / Math.PI) / 2.0 * n);
        var maxIndex = (int)n - 1;
        xTile = Math.Clamp(xTile, 0, maxIndex);
        yTile = Math.Clamp(yTile, 0, maxIndex);
        return (xTile, yTile);
    }

    private static double ClampLatitude(double latitude)
    {
        if (!double.IsFinite(latitude))
        {
            return 0;
        }

        return Math.Clamp(latitude, -MaxLatitude, MaxLatitude);
    }

    private static double ClampLongitude(double longitude)
    {
        if (!double.IsFinite(longitude))
        {
            return 0;
        }

        return Math.Clamp(longitude, -180.0, 180.0);
    }
}

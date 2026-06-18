using AlarmaApp.Models;

namespace AlarmaApp.Services.Interfaces;

public interface ILocationService
{
    event EventHandler<LocationSnapshot>? LocationUpdated;
    bool IsTracking { get; }

    // True when the OS-level location toggle is on (GPS or network provider available). Distinct
    // from app permission — the user can grant Alarma location access yet still have the whole
    // device's location switch turned off, in which case no fixes will ever arrive.
    bool IsLocationServiceEnabled();

    Task<LocationSnapshot?> GetLastKnownLocationAsync();

    // Demands a fresh, highest-accuracy fix within the given timeout (used by SOS). If the precise fetch
    // hangs, times out, or fails, it falls back immediately to the last-known location so a dispatch is
    // never left without coordinates.
    Task<LocationSnapshot?> GetBestLocationAsync(TimeSpan timeout);

    Task StartTrackingAsync(CancellationToken cancellationToken);
    Task StopTrackingAsync();
}

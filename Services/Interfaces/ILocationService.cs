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
    Task StartTrackingAsync(CancellationToken cancellationToken);
    Task StopTrackingAsync();
}

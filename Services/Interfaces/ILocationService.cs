using AlarmaApp.Models;

namespace AlarmaApp.Services.Interfaces;

public interface ILocationService
{
    event EventHandler<LocationSnapshot>? LocationUpdated;
    bool IsTracking { get; }
    Task<LocationSnapshot?> GetLastKnownLocationAsync();
    Task StartTrackingAsync(CancellationToken cancellationToken);
    Task StopTrackingAsync();
}

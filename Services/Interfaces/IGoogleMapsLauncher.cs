namespace AlarmaApp.Services.Interfaces;

public interface IGoogleMapsLauncher
{
    Task OpenRerouteAsync(double latitude, double longitude);
}

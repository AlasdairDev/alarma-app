namespace AlarmaApp.Services.Interfaces;

public interface IEarphoneService
{
    (bool IsConnected, string Details) GetConnectionStatus();
}

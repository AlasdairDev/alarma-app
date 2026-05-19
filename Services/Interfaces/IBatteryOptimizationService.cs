namespace AlarmaApp.Services.Interfaces;

public interface IBatteryOptimizationService
{
    bool IsIgnoringOptimizations();
    Task RequestIgnoreOptimizationsAsync();
}

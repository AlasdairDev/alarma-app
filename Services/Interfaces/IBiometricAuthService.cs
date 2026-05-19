namespace AlarmaApp.Services.Interfaces;

public interface IBiometricAuthService
{
    Task<bool> AuthenticateAsync(string reason, CancellationToken cancellationToken);
}

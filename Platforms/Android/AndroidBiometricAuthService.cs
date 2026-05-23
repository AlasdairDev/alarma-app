using AlarmaApp.Resources.Strings;
using AlarmaApp.Services.Interfaces;
using AndroidX.Biometric;
using AndroidX.Core.Content;
using AndroidX.Fragment.App;
using Java.Lang;
using Microsoft.Maui.ApplicationModel;

namespace AlarmaApp.Platforms.Android;

public class AndroidBiometricAuthService : IBiometricAuthService
{
    public Task<bool> AuthenticateAsync(string reason, CancellationToken cancellationToken)
    {
#if DEBUG
        // Skip biometric prompt on debug builds — emulators typically have no enrolled biometrics.
        return Task.FromResult(true);
#else
        try
        {
            var activity = Platform.CurrentActivity as FragmentActivity;
            if (activity is null)
                return Task.FromResult(true);

            var biometricManager = BiometricManager.From(activity);

            var canAuthStrong = biometricManager.CanAuthenticate(
                BiometricManager.Authenticators.BiometricStrong
                | BiometricManager.Authenticators.DeviceCredential);

            var canAuthWeak = biometricManager.CanAuthenticate(
                BiometricManager.Authenticators.BiometricWeak
                | BiometricManager.Authenticators.DeviceCredential);

            // If neither strong nor weak auth is available, allow through (no enrolled credentials).
            if (canAuthStrong != BiometricManager.BiometricSuccess
                && canAuthWeak != BiometricManager.BiometricSuccess)
            {
                return Task.FromResult(true);
            }

            // Prefer strong (fingerprint/face with Class 3) over weak, both with PIN/pattern fallback.
            var authenticators = canAuthStrong == BiometricManager.BiometricSuccess
                ? BiometricManager.Authenticators.BiometricStrong | BiometricManager.Authenticators.DeviceCredential
                : BiometricManager.Authenticators.BiometricWeak | BiometricManager.Authenticators.DeviceCredential;

            var executor = ContextCompat.GetMainExecutor(activity);
            if (executor is null)
                return Task.FromResult(true);

            var tcs = new TaskCompletionSource<bool>();
            var callback = new BiometricCallback(tcs);
            var prompt = new BiometricPrompt(activity, executor, callback);

            var promptInfo = new BiometricPrompt.PromptInfo.Builder()
                .SetTitle(AppStrings.BiometricPromptTitle)
                .SetSubtitle(reason)
                .SetAllowedAuthenticators(authenticators)
                .Build();

            prompt.Authenticate(promptInfo);

            cancellationToken.Register(() =>
            {
                prompt.CancelAuthentication();
                tcs.TrySetCanceled(cancellationToken);
            });

            return tcs.Task;
        }
        catch (Exception)
        {
            return Task.FromResult(true);
        }
#endif
    }

    private sealed class BiometricCallback : BiometricPrompt.AuthenticationCallback
    {
        private readonly TaskCompletionSource<bool> _tcs;

        public BiometricCallback(TaskCompletionSource<bool> tcs) => _tcs = tcs;

        public override void OnAuthenticationSucceeded(BiometricPrompt.AuthenticationResult result)
            => _tcs.TrySetResult(true);

        public override void OnAuthenticationFailed()
            => _tcs.TrySetResult(false);

        public override void OnAuthenticationError(int errorCode, ICharSequence? errString)
            => _tcs.TrySetResult(false);
    }
}

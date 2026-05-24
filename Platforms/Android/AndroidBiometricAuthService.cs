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
        try
        {
            var activity = Platform.CurrentActivity as FragmentActivity;
            if (activity is null)
                return Task.FromResult(false);

            var biometricManager = BiometricManager.From(activity);

            // BIOMETRIC_STRONG/WEAK | DEVICE_CREDENTIAL combined is only valid on API 30+.
            // API 26-29: that combination throws on PromptInfo.Build(); use biometric-only with a negative button.
            bool deviceCredentialSupported =
                global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.R;

            int authenticators;
            if (deviceCredentialSupported)
            {
                var canStrong = biometricManager.CanAuthenticate(
                    BiometricManager.Authenticators.BiometricStrong
                    | BiometricManager.Authenticators.DeviceCredential);
                var canWeak = biometricManager.CanAuthenticate(
                    BiometricManager.Authenticators.BiometricWeak
                    | BiometricManager.Authenticators.DeviceCredential);

                if (canStrong != BiometricManager.BiometricSuccess
                    && canWeak != BiometricManager.BiometricSuccess)
                    return Task.FromResult(false);

                authenticators = canStrong == BiometricManager.BiometricSuccess
                    ? BiometricManager.Authenticators.BiometricStrong
                      | BiometricManager.Authenticators.DeviceCredential
                    : BiometricManager.Authenticators.BiometricWeak
                      | BiometricManager.Authenticators.DeviceCredential;
            }
            else
            {
                // API 26-29: biometric-only (no DEVICE_CREDENTIAL combination).
                // If no biometric is enrolled on an older device, allow access rather than permanently locking.
                var canWeak = biometricManager.CanAuthenticate(
                    BiometricManager.Authenticators.BiometricWeak);
                if (canWeak != BiometricManager.BiometricSuccess)
                    return Task.FromResult(true);

                authenticators = BiometricManager.Authenticators.BiometricWeak;
            }

            var executor = ContextCompat.GetMainExecutor(activity);
            if (executor is null)
                return Task.FromResult(false);

            var tcs = new TaskCompletionSource<bool>();
            var prompt = new BiometricPrompt(activity, executor, new BiometricCallback(tcs));

            var promptBuilder = new BiometricPrompt.PromptInfo.Builder()
                .SetTitle(AppStrings.BiometricPromptTitle)
                .SetSubtitle(reason)
                .SetAllowedAuthenticators(authenticators);

            // DEVICE_CREDENTIAL provides its own dismiss path; biometric-only needs an explicit button.
            if (!deviceCredentialSupported)
                promptBuilder.SetNegativeButtonText(AppStrings.BiometricPromptCancel);

            prompt.Authenticate(promptBuilder.Build());

            cancellationToken.Register(() =>
            {
                prompt.CancelAuthentication();
                tcs.TrySetCanceled(cancellationToken);
            });

            return tcs.Task;
        }
        catch (System.Exception)
        {
            return Task.FromResult(false);
        }
    }

    private sealed class BiometricCallback : BiometricPrompt.AuthenticationCallback
    {
        private readonly TaskCompletionSource<bool> _tcs;

        public BiometricCallback(TaskCompletionSource<bool> tcs) => _tcs = tcs;

        public override void OnAuthenticationSucceeded(BiometricPrompt.AuthenticationResult result)
            => _tcs.TrySetResult(true);

        public override void OnAuthenticationFailed()
        {
            // Called on each failed scan attempt (e.g. wrong finger).
            // BiometricPrompt handles retries automatically — do NOT resolve the TCS here.
        }

        public override void OnAuthenticationError(int errorCode, ICharSequence? errString)
            => _tcs.TrySetResult(false);
    }
}

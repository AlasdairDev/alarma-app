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
            {
                return Task.FromResult(true); // Fail open if no activity
            }

            // Check if biometrics are available at all before trying
            var biometricManager = BiometricManager.From(activity);
            var canAuthenticate = biometricManager.CanAuthenticate(
                BiometricManager.Authenticators.BiometricStrong
                | BiometricManager.Authenticators.DeviceCredential);

            if (canAuthenticate != BiometricManager.BiometricSuccess)
            {
                // No biometrics or device credential enrolled — skip auth
                return Task.FromResult(true);
            }

            var executor = ContextCompat.GetMainExecutor(activity);
            if (executor is null)
            {
                return Task.FromResult(true);
            }

            var tcs = new TaskCompletionSource<bool>();
            var callback = new BiometricCallback(tcs);
            var prompt = new BiometricPrompt(activity, executor, callback);
            var promptInfo = new BiometricPrompt.PromptInfo.Builder()
                .SetTitle(AppStrings.BiometricPromptTitle)
                .SetSubtitle(reason)
                .SetAllowedAuthenticators(
                    BiometricManager.Authenticators.BiometricStrong
                    | BiometricManager.Authenticators.DeviceCredential)
                .Build();

            prompt.Authenticate(promptInfo);
            cancellationToken.Register(() =>
            {
                prompt.CancelAuthentication();
                tcs.TrySetCanceled(cancellationToken);
            });

            return tcs.Task;
        }
        catch (System.Exception)
        {
            // If anything goes wrong with biometrics, fail open so app still loads
            return Task.FromResult(true);
        }
    }

    private sealed class BiometricCallback : BiometricPrompt.AuthenticationCallback
    {
        private readonly TaskCompletionSource<bool> _tcs;

        public BiometricCallback(TaskCompletionSource<bool> tcs)
        {
            _tcs = tcs;
        }

        public override void OnAuthenticationSucceeded(BiometricPrompt.AuthenticationResult result)
        {
            _tcs.TrySetResult(true);
        }

        public override void OnAuthenticationFailed()
        {
            _tcs.TrySetResult(false);
        }

        public override void OnAuthenticationError(int errorCode, ICharSequence? errString)
        {
            _tcs.TrySetResult(false);
        }
    }
}
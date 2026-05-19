# Android integrations

This scaffold wires Android-native services through `Platforms/Android` to keep all device-only functionality isolated.

## Service mappings

- `AndroidLocationService` → `LocationManager`
- `AndroidSmsService` → `SmsManager`
- `AndroidAlarmAudioService` → `AudioManager`
- `AndroidAlarmNotificationService` → `NotificationManager`
- `AndroidBiometricAuthService` → `BiometricPrompt`
- `AndroidConnectivityService` → `ConnectivityManager`
- `AndroidGoogleMapsLauncher` → Intent handoff to the installed Google Maps app
- `AndroidBatteryOptimizationService` → `PowerManager` + system settings intent
- `AndroidEarphoneService` → `AudioManager` earphone status checks

## Permissions

See `Platforms/Android/AndroidManifest.xml` for the required runtime permissions and hardware features.

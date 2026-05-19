# ALARMA App (Android · .NET MAUI)

This repository contains the Android-only .NET MAUI scaffold for **ALARMA**, organized in an MVC style and built for an offline-first, on-device workflow.

## Target platform

- **Android smartphones only** (5.0–6.7" portrait form factor)
- **Minimum OS**: Android 8.0 (API 26)
- **Out of scope**: iOS, web, and desktop

## Tech stack

- **Framework**: .NET MAUI
- **Language**: C#
- **Architecture**: MVC
- **Local storage**:
  - `sqlite-net-pcl` for structured data (trip history, saved routes, emergency contacts, behavioral profiles)
  - MAUI `Preferences` API for lightweight key-value settings (alarm sound, onboarding flags)

## Offline-first behavior

All user data is stored on-device only to align with RA 10173 (Data Privacy Act of the Philippines). There is no backend server or cloud database.

## External APIs and native utilities

- **Nominatim API**: HTTPS GET for text destination search and geocoding before a trip begins
- **OpenStreetMap**: map tile downloads during initial destination setup
- **Google Maps handoff**: Android intent to open the installed Google Maps app for rerouting
- **Android native APIs**:
  - `LocationManager` for continuous GPS tracking and distance calculations
  - `SmsManager` for emergency SOS text alerts
  - `AudioManager` + `NotificationManager` for critical alarms that override silent/DND
  - `Vibrator` for vibration-only alarm stages
  - `BiometricPrompt` for PIN/biometric unlock on launch
  - `ConnectivityManager` for network availability checks
  - `PowerManager` for battery optimization prompts

## Feature set implemented in this repository

- GPS-based real-time location tracking
- Multi-stage alarm system (3 stages)
- Adaptive alarm trigger distance
- Automatic alarm volume and vibration
- Vibration-only mode
- Alarm sound selection
- Overshoot detection
- Rerouting screen after overshoot
- Emergency SOS via Native Android SMS
- Saved favorite routes (up to 5)
- Travel history dashboard (last 20 trips)
- Destination search via Nominatim API
- OpenStreetMap display
- Offline functionality
- Load availability notification
- PIN or biometric authentication
- In-app export and data backup
- Battery optimization
- Emergency contact management
- Earphone connection status

## Project structure (MVC)

```
Controllers/
Models/
Views/
Services/
Platforms/Android/
Resources/
```

## Getting started

1. Install the .NET 9 SDK and the MAUI Android workload.

   ```bash
   dotnet workload install maui-android
   ```

2. Restore dependencies and build for Android.

   ```bash
   dotnet restore
   dotnet build -f net9.0-android
   ```

3. Run on an Android device or emulator.

   ```bash
   dotnet build -t:Run -f net9.0-android
   ```

## Notes

- The scaffold is intentionally minimal and focuses on the required integrations.
- Runtime permission prompts are included for location, SMS, and notifications, and the app requests DND access when critical audio is triggered.
- Trip tracking uses a foreground location service to keep GPS updates active in the background.
- Use the services in `Platforms/Android` to wire deeper Android-specific behavior.

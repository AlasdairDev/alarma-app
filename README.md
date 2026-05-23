# Alarma — Android Safety Alarm App (.NET MAUI)

Alarma is an **offline-first** Android safety app that tracks your commute via GPS and fires a multi-stage alarm when you are approaching, arriving at, or overshooting your destination. All user data is stored exclusively on-device to comply with RA 10173 (Data Privacy Act of the Philippines).

---

## Target platform

| Property | Value |
|---|---|
| Platform | Android only (portrait, 5.0–6.7 inch smartphones) |
| Minimum OS | Android 8.0 (API 26) |
| Target OS | Android 15 (API 35) |
| Out of scope | iOS, web, desktop |

---

## Tech stack

| Layer | Technology |
|---|---|
| Framework | .NET MAUI (.NET 9) |
| Language | C# |
| Architecture | MVC |
| Local DB | `sqlite-net-pcl` — trip history, saved routes, emergency contacts, behavioral profiles |
| Key-value store | MAUI `Preferences` API — alarm sound, lead time, onboarding status |
| Build tooling | `UseInterpreter=true` for faster debug iteration on Android |

---

## External APIs and native integrations

Alarma makes **no outbound requests to a proprietary backend**. The only network calls are to open-source or device-native services:

| Integration | Purpose |
|---|---|
| **Nominatim API** (HTTPS GET) | Text-based destination geocoding before a trip begins. Requires internet. |
| **OpenStreetMap tile server** | Downloads a single map tile thumbnail during destination setup. Requires internet. |
| **Google Maps Intent** | Launches the installed Google Maps app for rerouting on overshoot. No API key needed. |
| **Android `LocationManager`** | Continuous background GPS tracking via a foreground `Service`. Works offline. |
| **Android `SmsManager`** | Sends emergency SOS messages with GPS coordinates over cellular. No internet needed. |
| **`AudioManager` + `NotificationManager`** | Fires multi-stage alarms, overrides silent/DND mode when critical. |
| **`Vibrator`** | Vibration-only alarm stages for users who disable ringtones. |
| **`BiometricPrompt`** | PIN or biometric unlock when the app launches (release builds only). |
| **`ConnectivityManager`** | Checks network availability before destination search and tile download. |
| **`PowerManager`** | Requests battery-optimization exemption to keep the foreground service alive. |

---

## Feature set

| Feature | Status |
|---|---|
| GPS-based real-time location tracking | Implemented |
| Background foreground location service | Implemented |
| Multi-stage alarm system (3 stages) | Implemented |
| Adaptive alarm trigger distance (speed-based) | Implemented |
| Automatic alarm volume escalation per stage | Implemented |
| Vibration-only mode | Implemented |
| Alarm sound selection (Default / Alarm / Notification / Ringtone) | Implemented |
| DND and silent mode override for critical alarms | Implemented |
| Overshoot detection | Implemented |
| Route deviation detection | Implemented |
| Google Maps rerouting on overshoot | Implemented |
| Emergency SOS via native SMS (no internet required) | Implemented |
| Emergency contact management (SQLite) | Implemented |
| Saved favorite routes (up to 5, SQLite) | Implemented |
| Travel history dashboard (last 20 trips, SQLite) | Implemented |
| Destination search via Nominatim API | Implemented |
| OpenStreetMap tile display | Implemented |
| Offline-first operation | Implemented |
| Load availability notification (online ↔ offline transition) | Implemented |
| PIN or biometric authentication (BiometricPrompt, release builds) | Implemented |
| In-app JSON backup and restore | Implemented |
| Battery optimization exemption request | Implemented |
| Earphone connection detection | Implemented |

> **Note on biometric auth:** The `BiometricPrompt` flow is active in **release builds only**. Debug builds bypass authentication automatically so the app can run on emulators that have no enrolled biometrics.

---

## Project structure

```
AlarmaApp/
├── Controllers/
│   └── HomeController.cs          # Business logic, INotifyPropertyChanged, all ICommand bindings
├── Models/
│   ├── AlarmStage.cs              # Stage1 / Stage2 / Stage3 enum
│   ├── BackupPayload.cs           # JSON backup schema
│   ├── BehavioralProfile.cs       # Saved alarm preset (name, lead time, sound)
│   ├── EmergencyContact.cs        # Name, phone number, IsPrimary flag
│   ├── LocationSnapshot.cs        # Lat/lon, accuracy, timestamp
│   ├── SavedRoute.cs              # Destination name, coordinates
│   └── TripHistory.cs             # Start/end time, distance, overshoot flag, summary
├── Views/
│   └── HomeView.xaml(.cs)         # Single-page compiled-binding MAUI view
├── Services/
│   ├── BackupService.cs           # Export and restore JSON backup
│   ├── DatabaseService.cs         # SQLite CRUD via sqlite-net-pcl
│   ├── GeocodingService.cs        # Nominatim HTTPS search
│   ├── OpenStreetMapTileService.cs# OSM tile URI generator (Web Mercator)
│   ├── PermissionsService.cs      # Runtime permission helpers
│   ├── PreferencesService.cs      # MAUI Preferences wrapper
│   └── Interfaces/                # Service contracts (ISmsService, ILocationService, …)
├── Platforms/Android/
│   ├── AndroidAlarmAudioService.cs        # AudioManager, volume, ringtone, vibration
│   ├── AndroidAlarmNotificationService.cs # NotificationManager channel + alerts
│   ├── AndroidBatteryOptimizationService.cs
│   ├── AndroidBiometricAuthService.cs     # BiometricPrompt (strong → weak → DeviceCredential)
│   ├── AndroidConnectivityService.cs
│   ├── AndroidEarphoneService.cs          # AudioManager.GetDevices() wired + BT detection
│   ├── AndroidGoogleMapsLauncher.cs       # google.navigation: intent + geo: fallback
│   ├── AndroidLocationService.cs          # Wraps LocationTrackingService foreground service
│   ├── AndroidSmsService.cs               # SmsManager direct SMS
│   ├── LocationTrackingService.cs         # Android Service + ILocationListener, GPS+Network
│   ├── MainActivity.cs
│   └── AndroidManifest.xml
└── Resources/
    ├── Strings/AppStrings.cs      # BiometricPromptTitle, BiometricPromptCancel
    └── Styles/                    # Colors.xaml, Styles.xaml
```

---

## Getting started

### Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9)
- Android SDK (API 26–35), installed via Visual Studio or `sdkmanager`
- JDK 17
- A physical Android device or an x86_64 emulator running API 26+

### Install the MAUI Android workload

```bash
dotnet workload install maui-android
```

### Restore and build

```bash
dotnet restore
dotnet build -f net9.0-android
```

### Deploy to a connected device or emulator

```bash
dotnet build -t:Run -f net9.0-android
```

### Release build (enables biometric auth)

```bash
dotnet publish -f net9.0-android -c Release
```

---

## Permissions required

| Permission | Reason |
|---|---|
| `ACCESS_FINE_LOCATION` | GPS tracking |
| `ACCESS_COARSE_LOCATION` | Network-assisted location |
| `ACCESS_BACKGROUND_LOCATION` | Keep tracking when the screen is off |
| `FOREGROUND_SERVICE` + `FOREGROUND_SERVICE_LOCATION` | Background location service |
| `SEND_SMS` | Emergency SOS messages |
| `POST_NOTIFICATIONS` | Trip alerts and alarm notifications |
| `MODIFY_AUDIO_SETTINGS` + `VIBRATE` | Alarm volume and vibration control |
| `ACCESS_NOTIFICATION_POLICY` | Override DND for critical alarms |
| `USE_BIOMETRIC` | BiometricPrompt authentication |
| `REQUEST_IGNORE_BATTERY_OPTIMIZATIONS` | Prevent OS from killing the location service |

---

## Privacy

No data leaves the device except:
- Destination text queries sent to the Nominatim public API over HTTPS.
- Map tile image requests sent to OpenStreetMap tile servers over HTTPS.

Emergency SMS messages are sent over the cellular network directly from `SmsManager` — they do not pass through any Alarma server.

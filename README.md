# Alarma — Adaptive Anti-Oversleep Destination Alarm and Emergency Safety System

Alarma is an **offline-first** Android safety app built for Metro Manila jeepney, UV Express, and city bus commuters. It tracks your commute via GPS, fires a multi-stage escalating alarm as you approach your stop, detects overshoot, and provides emergency safety tools. All user data is stored exclusively on-device in compliance with RA 10173 (Data Privacy Act of the Philippines).

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
| Local DB | `sqlite-net-pcl` — trip history, saved routes, emergency contacts |
| Key-value store | MAUI `Preferences` API — alarm settings, vehicle type, vibration, onboarding state |
| Map rendering | Leaflet.js + CartoDB dark tiles in a MAUI `WebView` |
| Build tooling | `UseInterpreter=true` for faster debug iteration on Android |

---

## External APIs and native integrations

Alarma makes **no outbound requests to a proprietary backend**. The only network calls are to open-source or device-native services:

| Integration | Purpose |
|---|---|
| **Photon API** (photon.komoot.io) | Primary geocoding — Elasticsearch-backed, fuzzy matching, location-biased to Metro Manila. No API key. |
| **Nominatim API** (nominatim.openstreetmap.org) | Fallback geocoding when Photon returns fewer than 3 results. No API key. |
| **Philippine alias expansion** | Client-side dictionary expands abbreviations (BGC, MOA, NAIA, CDO, MRT, etc.) before querying. |
| **CartoDB tile server via Leaflet.js** | Renders interactive OpenStreetMap in a MAUI WebView. Requires internet. |
| **Google Maps Intent** | Launches the installed Google Maps app for rerouting on overshoot. No API key. |
| **Android `LocationManager`** | Continuous background GPS tracking via a foreground `Service`. Works offline. |
| **Android `SmsManager`** | Sends emergency SOS messages with GPS coordinates and a Google Maps link over cellular. No internet needed. |
| **`AudioManager` + `NotificationManager`** | Fires multi-stage alarms, overrides silent/DND mode when critical. |
| **`Vibrator`** | Vibration-only alarm stages for users who disable ringtones. |
| **`BiometricPrompt`** | PIN or biometric unlock on app launch (release builds). |
| **`ConnectivityManager`** | Checks network availability before destination search and tile download. |
| **`PowerManager`** | Requests battery-optimization exemption to keep the foreground service alive. |
| **`AudioManager.GetDevices()`** | Detects wired and Bluetooth earphone connections. |

---

## Feature set

| Feature | Status | Notes |
|---|---|---|
| GPS-based real-time location tracking | ✅ | Haversine distance, adaptive speed calculation |
| Background foreground location service | ✅ | Persistent notification, survives screen-off |
| Multi-stage alarm system (3 stages) | ✅ | Stage 1 → Stage 2 → Emergency full-screen |
| Adaptive alarm trigger distance | ✅ | Speed × lead time, vehicle-type minimum distances |
| Vehicle type selection | ✅ | Jeepney (300 m min), UV Express (500 m), City Bus (400 m) |
| Alarm volume escalation per stage | ✅ | Via `AudioManager` alarm channel priority |
| Vibration intensity (Low / Medium / High) | ✅ | Picker in Settings → ALARM; scales vibration pulse durations (Low: 0.5×, Medium: 1×, High: 1.5×) passed through to `AndroidAlarmAudioService` |
| Vibration-only mode | ✅ | Toggle in Settings → ALARM; suppresses ringtone |
| Alarm sound selection (5 options) | ✅ | Picker in Settings → ALARM; Default / Alarm / Chime / Notification / Ringtone |
| Alarm lead time (1–60 min) | ✅ | Stepper in Settings → ALARM; controls adaptive trigger distance |
| Alarm snooze (max 3, then escalates) | ✅ | Auto-escalates to Stage 3 after 3 snoozes |
| DND and silent mode override | ✅ | Stage 2 + Stage 3 use alarm channel priority |
| Overshoot detection | ✅ | Consecutive distance monitoring past stop |
| Route deviation detection | ✅ | Alerts when moving away from destination |
| Google Maps rerouting on overshoot | ✅ | Android Intent with pre-filled destination coordinates |
| Destination search — dual-source geocoding | ✅ | Photon (primary, fuzzy) + Nominatim (fallback) |
| Philippine abbreviation expansion | ✅ | BGC, MOA, NAIA, CDO, MRT, EDSA, Greenbelt, etc. |
| Place-type subtitles in search results | ✅ | Hospital, Fast Food, University, Barangay, Street, etc. |
| As-you-type search with 500 ms debounce | ✅ | Spinner shown during search |
| Map destination pin with popup | ✅ | Map pans to selection at zoom 15 after picking a result |
| Live user location dot on map | ✅ | Blue circle updated every 5 s while tracking |
| Center-on-me FAB | ✅ | Tapping the circle button pans map to current GPS position |
| Emergency SOS — 2-second hold activation | ✅ | Prevents accidental sends |
| SOS sends to all emergency contacts simultaneously | ✅ | All saved contacts, not just primary |
| SOS message includes GPS + Google Maps link | ✅ | `https://maps.google.com/?q=LAT,LON` in SMS body |
| SOS rate limiter | ✅ | 30-second cooldown between sends |
| Emergency contact management (up to 3) | ✅ | Managed directly from the Emergency tab; Philippine number format validated (09X / +639X) |
| Star / primary contact designation | ✅ | Tap the star on any contact row in the Emergency tab to promote it |
| Saved favorite routes (up to 5) | ✅ | Route name 2–30 characters; tap to apply, star to remove |
| Save destination shortcut | ✅ | Star icon in the destination card on the Home screen |
| Travel history dashboard (last 20 trips) | ✅ | Start time, destination, duration, distance, overshoot badge |
| Trip history badges | ✅ | Alarm stage reached, km traveled, duration, Missed Stop |
| Launch splash screen | ✅ | `LaunchView` is the initial `MainPage`; zero flash of the Shell before the splash appears. Tap or ~1.8 s auto-advance (400 ms fade-in + 1 400 ms hold + 280 ms fade-out) |
| Entrance animations — all pages | ✅ | `Content`-layer fade-in/slide-up on entry, fade-out/slide-down on exit; Shell's native page animation is disabled so only one animation system runs at a time — no jank |
| Onboarding tutorial (4 slides) | ✅ | Shown on first launch after splash; swipe or tap to advance |
| PIN or biometric authentication | ✅ | `BiometricPrompt` on launch (release builds); API 30+ uses BiometricStrong + DeviceCredential, API 26–29 falls back to BiometricWeak only |
| Biometric / PIN setup in Settings | ✅ | AUTHENTICATION section — shows enrollment status, deep-links to Android biometric enrollment and security settings |
| In-app AES-GCM encrypted backup and restore | ✅ | Exports to local device storage; restore validates phone numbers, name lengths, and coordinate bounds before inserting |
| Battery optimization exemption request | ✅ | Prevents OS from killing the GPS service |
| Earphone connection detection | ✅ | Wired and Bluetooth via `AudioManager.GetDevices()` |
| Offline-first operation | ✅ | GPS, alarms, SOS, saved routes all work without internet |
| Load availability notification | ✅ | Online ↔ offline transition banner |

---

## Navigation structure

```
App launch
└── LaunchView (initial MainPage — shown before the Shell ever attaches)
    └── [after animation] → AppShell loads, navigates to //home

Shell (TabBar)
├── History       — trip history with Departure → Destination cards, distance/duration/overshoot badges
├── Favorites     — saved routes for one-tap trip starts; star tap on Home destination card saves here
├── Home          — map, destination search, Start Trip card, live user dot, center-on-me FAB
├── Emergency     — SOS press-and-hold button; emergency contact list, add/remove/star contacts inline
└── Settings      — alarm sound/lead/vibration, battery optimisation, backup/restore, biometric/PIN setup

Modal routes (push navigation, tab bar hidden)
├── search        — full-screen destination search with Photon + Nominatim autocomplete
├── alarmstage    — full-screen alarm stage UI (dismiss / snooze / stop trip)
└── onboarding    — first-launch 4-slide tutorial (swipeable)
```

---

## Project structure

```
AlarmaApp/
├── Controllers/
│   └── HomeController.cs          # All business logic, INotifyPropertyChanged, ICommand bindings
├── Models/
│   ├── AlarmStage.cs              # Stage1 / Stage2 / Stage3 enum
│   ├── BackupPayload.cs           # AES-GCM encrypted backup schema
│   ├── BehavioralProfile.cs       # Adaptive profile model (reaction time, snooze frequency)
│   ├── EmergencyContact.cs        # Name, phone number, IsPrimary flag
│   ├── LocationSnapshot.cs        # Lat/lon, accuracy, timestamp
│   ├── SavedRoute.cs              # Destination name, coordinates
│   └── TripHistory.cs             # Start/end time, distance, overshoot, alarm stage, snooze count
├── Views/
│   ├── HomeView.xaml(.cs)         # Map, search pill, Start Trip card, location FAB
│   ├── SearchView.xaml(.cs)       # Full-screen search with debounced autocomplete
│   ├── AlarmStageView.xaml(.cs)   # Full-screen alarm dismiss/snooze UI
│   ├── EmergencyView.xaml(.cs)    # SOS hold-to-activate + inline emergency contact management
│   ├── FavoritesView.xaml(.cs)    # Saved routes list
│   ├── HistoryView.xaml(.cs)      # Trip history cards
│   ├── SettingsView.xaml(.cs)     # Alarm, backup, permissions settings
│   ├── LaunchView.xaml(.cs)       # Branded splash (initial MainPage, not a Shell route)
│   └── OnboardingView.xaml(.cs)   # 4-slide first-launch tutorial
├── Services/
│   ├── BackupService.cs           # AES-GCM export and restore
│   ├── DatabaseService.cs         # SQLite CRUD via sqlite-net-pcl
│   ├── GeocodingService.cs        # Photon (primary) + Nominatim (fallback) with PH alias expansion
│   ├── PermissionsService.cs      # Runtime permission helpers
│   ├── PreferencesService.cs      # MAUI Preferences wrapper
│   └── Interfaces/                # ISmsService, ILocationService, IAlarmAudioService, etc.
├── Platforms/Android/
│   ├── AndroidAlarmAudioService.cs        # AudioManager, volume, ringtone, vibration
│   ├── AndroidAlarmNotificationService.cs # NotificationManager channel + alerts
│   ├── AndroidBatteryOptimizationService.cs
│   ├── AndroidBiometricAuthService.cs     # BiometricPrompt (strong → weak → DeviceCredential)
│   ├── AndroidConnectivityService.cs
│   ├── AndroidEarphoneService.cs          # AudioManager.GetDevices() wired + BT detection
│   ├── AndroidGoogleMapsLauncher.cs       # google.navigation: intent + geo: fallback
│   ├── AndroidLocationService.cs          # Wraps LocationTrackingService foreground service
│   ├── AndroidSmsService.cs               # SmsManager direct SMS (re-validates PH number format)
│   ├── LocationTrackingService.cs         # Android Service + ILocationListener, GPS+Network
│   ├── MainActivity.cs
│   ├── MainApplication.cs
│   └── AndroidManifest.xml
├── AppShell.xaml(.cs)             # TabBar + modal route registration (search, alarmstage, onboarding)
└── Resources/
    ├── Images/                    # launch.png (splash), tutorial1–4.png (onboarding slides)
    └── Styles/                    # Colors.xaml, Styles.xaml
```

---

## Security

Alarma has been through a full DevSecOps / SSDLC audit. Key hardening measures:

| Area | Measure |
|---|---|
| **Local database** | AES-256 key generated by `RandomNumberGenerator`, stored in Android `SecureStorage` (Keystore-backed). Never in `Preferences` or hardcoded. |
| **Backup encryption** | AES-256-GCM with a random 12-byte nonce per export. The GCM authentication tag is verified before any JSON is deserialized — tampered or corrupted files are rejected before a single byte of plaintext is read. |
| **Backup restore validation** | All records are validated **before** any database table is cleared. This prevents data loss when a tampered or empty backup passes decryption but contains zero valid records. Contacts are filtered by Philippine number format and name length (≤ 50 chars). Routes are validated for name length (2–30 chars) and coordinates within the Philippines bounding box. Quantity caps (3 contacts / 5 routes / 100 history / 20 profiles) prevent unbounded inserts. |
| **Biometric auth** | `OnAuthenticationFailed()` is a no-op — BiometricPrompt handles retries internally; a single bad scan does not close the prompt. `OnAuthenticationError()` resolves false. No fail-open paths. |
| **Biometric API compatibility** | `BiometricStrong + DeviceCredential` combined is API 30+ only. API 26–29 uses `BiometricWeak` with a negative cancel button to avoid a crash on `PromptInfo.Build()`. If no biometric is enrolled on an older device, access is allowed rather than permanently locking the user out. |
| **SOS hold timer cleanup** | `EmergencyView.OnDisappearing` stops and nulls the 2-second hold timer so a page transition (e.g. alarm pushing over the Emergency tab) cannot silently fire SOS on a hidden page. |
| **SOS rate limiter** | 30-second cooldown enforced in `HomeController`; phone number format re-validated in `AndroidSmsService` at the transport layer (defense in depth). |
| **Google Maps coordinates** | Formatted with `InvariantCulture` + `"F6"` to prevent locale-specific decimal separators (commas) breaking the navigation URI on non-English devices. |
| **Map popup label** | Destination name injected into Leaflet JS via `JsonSerializer.Serialize()`, which HTML-escapes `<`, `>`, `'` as `\uXXXX` — prevents script injection via API-sourced place names. |
| **WebView Content Security Policy** | Both map HTML templates include `<meta http-equiv="Content-Security-Policy">` restricting scripts to `unpkg.com`, images to CartoDB/OSM tile hosts, and blocking all outbound `connect-src` from the WebView context. |
| **Captive portal detection** | `NetCapability.Validated` checked alongside `Internet` so a captive-portal WiFi does not produce a false "online" reading. |
| **Ringer mode restore** | `_savedRingerMode` saved with `??=` before Stage 2+ overrides; restored in `DisableCriticalAudioAsync`. A `CancellationTokenSource` per ringtone play ensures a superseded alarm does not restore ringer mode and silence a still-playing escalated alarm. |
| **Search cancellation** | Each `SearchDestinationAsync` call cancels the previous in-flight HTTP request via `CancellationTokenSource`. `GeocodingService` re-throws `OperationCanceledException` so stale responses are silently dropped rather than overwriting current results. |
| **Contact name length** | Capped at 50 characters at add-time (`AddEmergencyContactAsync`) and at restore-time (`RestoreLatestAsync`). |
| **Display name length** | External API display names capped at 200 characters before being stored as `DestinationSummaryText`. |
| **Emergency contact max** | Hard cap of 3 contacts enforced at add-time and at backup restore. |
| **Biometric timeout** | `CancellationTokenSource(60s)` wraps the `AuthenticateAsync` call — prevents an infinite hang if the OS never calls back. |
| **Debug bypass** | `#if !DEBUG` guard around the biometric gate so debug builds on emulators (no PIN enrolled) are not permanently locked. |
| **Notification null guard** | `Notification.Builder.Build()` result is null-checked before `NotificationManager.Notify()` — prevents a potential `NullReferenceException` under low-resource conditions. |
| **TripHistory restore validation** | Each record in a restored backup is checked for: date range (2020-01-01 to now+1d), `DistanceMeters` ≤ 1 000 km, `MaxAlarmStageReached` ∈ {0–3}, `SnoozeCount` ≤ 100, `DestinationName`/`Summary` within length caps, and coordinates within ±90/±180. Records that fail any check are silently discarded before insertion. |
| **BehavioralProfile restore validation** | Restored profiles are checked for: non-empty name ≤ 50 chars, `AlarmLeadMinutes` clamped to 1–60, `AlarmSound` in the allowed whitelist {Default, Alarm, Chime, Notification, Ringtone}, `Notes` ≤ 300 chars. |
| **Preferences clamping on restore** | `AlarmLeadMinutes` is clamped to 1–60 and `AlarmSound` is whitelisted before writing to `Preferences`, so storage never holds a raw unvalidated value even transiently between restore and next app init. |
| **EmergencyView UI length enforcement** | `Entry` fields enforce `MaxLength="50"` (name) and `MaxLength="13"` (phone) at the Android input layer, preventing large strings from reaching the controller and consuming memory before validation fires. |

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
- Destination text queries sent to Photon (photon.komoot.io) and Nominatim (nominatim.openstreetmap.org) over HTTPS.
- Map tile image requests sent to CartoDB/OpenStreetMap tile servers over HTTPS.

Emergency SOS messages are sent directly from Android `SmsManager` over the cellular network — they do not pass through any Alarma server. All GPS coordinates, emergency contacts, trip history, and behavioral data are stored exclusively on the user's device in a local SQLite database encrypted at the application layer, in compliance with RA 10173 (Data Privacy Act of the Philippines).

---

## Capstone project

Alarma is a PUP (Polytechnic University of the Philippines) BSIT 3-4 capstone project by Keith Justin S. Ababao, Kyla J. Barbin, Roje Alasdair T. Evangelista, and Pauline R. Lacanilaο.

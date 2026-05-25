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
| Local DB | `sqlite-net-pcl` + SQLCipher — AES-256 encrypted SQLite for trip history, saved routes, emergency contacts |
| Key-value store | MAUI `Preferences` API — alarm settings, vehicle type, vibration, onboarding state |
| Map rendering | Leaflet.js + CartoDB dark tiles in a MAUI `WebView` with strict Content Security Policy |
| Build tooling | Debug: `UseInterpreter=true` for faster iteration. Release: AOT compilation + SDK-only linker |

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
| **`ConnectivityManager`** | Checks `NetCapability.Validated` before destination search to avoid false-positive on captive-portal WiFi. |
| **`PowerManager`** | Requests battery-optimization exemption to keep the foreground service alive. |
| **`AudioManager.GetDevices()`** | Detects wired and Bluetooth earphone connections. |

---

## Feature set

| Feature | Status | Notes |
|---|---|---|
| GPS-based real-time location tracking | ✅ | Haversine distance, adaptive speed calculation |
| Background foreground location service | ✅ | Persistent notification, survives screen-off; `Exported=false`, `ForegroundServiceType=TypeLocation` |
| Multi-stage alarm system (3 stages) | ✅ | Stage 1 → Stage 2 → Emergency full-screen |
| Adaptive alarm trigger distance | ✅ | Speed × lead time, vehicle-type minimum distances |
| Vehicle type selection | ✅ | Jeepney (300 m min), UV Express (500 m), City Bus (400 m) |
| Alarm volume escalation per stage | ✅ | Via `AudioManager` alarm channel priority |
| Vibration intensity (Low / Medium / High) | ✅ | Picker in Settings → ALARM; scales vibration pulse durations (Low: 0.5×, Medium: 1×, High: 1.5×) |
| Vibration-only mode | ✅ | Toggle in Settings → ALARM; suppresses ringtone |
| Alarm sound selection (5 options) | ✅ | Picker in Settings → ALARM; Default / Alarm / Chime / Notification / Ringtone |
| Alarm lead time (1–60 min) | ✅ | Stepper in Settings → ALARM; controls adaptive trigger distance |
| Alarm snooze (max 3, then escalates) | ✅ | Auto-escalates to Stage 3 after 3 snoozes |
| DND and silent mode override | ✅ | Stage 2 + Stage 3 use alarm channel priority; ringer mode restored on dismiss |
| Overshoot detection | ✅ | Consecutive distance monitoring past stop |
| Route deviation detection | ✅ | Alerts when moving away from destination |
| Google Maps rerouting on overshoot | ✅ | Android Intent with pre-filled destination coordinates (InvariantCulture formatted) |
| Destination search — dual-source geocoding | ✅ | Photon (primary, fuzzy) + Nominatim (fallback) |
| Philippine abbreviation expansion | ✅ | BGC, MOA, NAIA, CDO, MRT, EDSA, Greenbelt, etc. |
| Place-type subtitles in search results | ✅ | Hospital, Fast Food, University, Barangay, Street, etc. |
| As-you-type search with 500 ms debounce | ✅ | Spinner shown during search; Entry capped at MaxLength=200 |
| Map destination pin with popup | ✅ | Map pans to selection at zoom 15 after picking a result |
| Live user location dot on map | ✅ | Blue circle updated every 5 s while tracking |
| Center-on-me FAB | ✅ | Tapping the circle button pans map to current GPS position |
| Emergency SOS — 2-second hold activation | ✅ | Prevents accidental sends; timer nulled on page disappear |
| SOS sends to all emergency contacts simultaneously | ✅ | All saved contacts, not just primary |
| SOS message includes GPS + Google Maps link | ✅ | `https://maps.google.com/?q=LAT,LON` in SMS body |
| SOS rate limiter | ✅ | 30-second cooldown between sends |
| Emergency contact management (up to 3) | ✅ | Managed directly from the Emergency tab; Philippine number format validated (09X / +639X); MaxLength enforced at XAML layer |
| Star / primary contact designation | ✅ | Tap the star on any contact row in the Emergency tab to promote it |
| Saved favorite routes (up to 5) | ✅ | Route name 2–30 characters; tap to apply, star to remove |
| Save destination shortcut | ✅ | Star icon in the destination card on the Home screen |
| Travel history dashboard (last 20 trips) | ✅ | Start time, destination, duration, distance, overshoot badge |
| Trip history badges | ✅ | Alarm stage reached, km traveled, duration, Missed Stop |
| Launch splash screen | ✅ | `LaunchView` is the initial `MainPage`; zero flash of the Shell. Tap or ~1.7 s auto-advance (300 ms fade-in + 1 200 ms hold + 220 ms fade-out) |
| Entrance animations — all pages | ✅ | `Content`-layer fade-in/slide-up on entry, fade-out/slide-down on exit; Shell's native animation disabled |
| Onboarding tutorial (4 slides) | ✅ | Shown on first launch after splash; swipe left/right or tap to advance |
| PIN or biometric authentication | ✅ | `BiometricPrompt` on launch (release builds); API 30+ uses BiometricStrong + DeviceCredential, API 26–29 falls back to BiometricWeak only |
| Biometric / PIN setup in Settings | ✅ | AUTHENTICATION section — shows enrollment status, deep-links to Android biometric enrollment and security settings |
| In-app AES-GCM encrypted backup and restore | ✅ | Exports to local device storage; all records validated before DB clear; restore validates phone numbers, name lengths, coordinate bounds |
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
│   ├── SearchView.xaml(.cs)       # Full-screen search with debounced autocomplete; MaxLength=200
│   ├── AlarmStageView.xaml(.cs)   # Full-screen alarm dismiss/snooze UI; hardware back overridden
│   ├── EmergencyView.xaml(.cs)    # SOS hold-to-activate + inline emergency contact management
│   ├── FavoritesView.xaml(.cs)    # Saved routes list
│   ├── HistoryView.xaml(.cs)      # Trip history cards
│   ├── SettingsView.xaml(.cs)     # Alarm, backup, permissions settings
│   ├── LaunchView.xaml(.cs)       # Branded splash (initial MainPage, not a Shell route)
│   └── OnboardingView.xaml(.cs)   # 4-slide first-launch tutorial
├── Services/
│   ├── BackupService.cs           # AES-256-GCM export and restore with validate-before-clear
│   ├── DatabaseService.cs         # SQLite CRUD via sqlite-net-pcl + SQLCipher
│   ├── GeocodingService.cs        # Photon (primary) + Nominatim (fallback) with PH alias expansion
│   ├── PermissionsService.cs      # Runtime permission helpers
│   ├── PreferencesService.cs      # MAUI Preferences wrapper
│   └── Interfaces/                # ISmsService, ILocationService, IAlarmAudioService, etc.
├── Platforms/Android/
│   ├── AndroidAlarmAudioService.cs        # AudioManager, volume, ringtone, vibration
│   ├── AndroidAlarmNotificationService.cs # NotificationManager channel + alerts
│   ├── AndroidBatteryOptimizationService.cs
│   ├── AndroidBiometricAuthService.cs     # BiometricPrompt (strong → weak → DeviceCredential)
│   ├── AndroidConnectivityService.cs      # Validated capability check
│   ├── AndroidEarphoneService.cs          # AudioManager.GetDevices() wired + BT detection
│   ├── AndroidGoogleMapsLauncher.cs       # google.navigation: intent + geo: fallback
│   ├── AndroidLocationService.cs          # Wraps LocationTrackingService foreground service
│   ├── AndroidSmsService.cs               # SmsManager direct SMS (re-validates PH number format)
│   ├── LocationTrackingService.cs         # Android Service + ILocationListener, GPS+Network, Exported=false
│   ├── MainActivity.cs
│   ├── MainApplication.cs
│   └── AndroidManifest.xml
├── AlarmaApp.Tests/
│   ├── AlarmaApp.Tests.csproj     # xUnit, net9.0 (no Android/MAUI SDK required)
│   └── ValidationTests.cs         # Security-centric boundary tests: phone regex, coordinates,
│                                  #   sound whitelist, lead-time clamping, backup caps, GCM format
├── AppShell.xaml(.cs)             # TabBar + modal route registration (search, alarmstage, onboarding)
└── Resources/
    ├── Images/                    # launch_bg.jpg (splash background), main_logo.png (logo),
    │                              #   alarma_app_icon.png (app icon), tutorial1–4.png (onboarding slides)
    └── Styles/                    # Colors.xaml, Styles.xaml
```

---

## Security

Alarma has been through a full DevSecOps / SSDLC audit with OWASP Top 10 alignment. All Security Considerations are documented in the file-level comment header of every modified module.

### OWASP Top 10 alignment

| OWASP ID | Concern | Mitigation |
|---|---|---|
| **A01** Broken Access Control | Biometric gate bypass, IDOR on local data | `#if !DEBUG` biometric guard; onboarding gate enforced before `InitializeAsync`; all DB records are device-local with no cross-user surface |
| **A02** Cryptographic Failures | Weak encryption, hardcoded keys | AES-256-GCM for backup (random 96-bit nonce per export); AES-256 for SQLite; keys generated by `RandomNumberGenerator.GetBytes(32)` and stored in Android Keystore via `SecureStorage` |
| **A03** Injection | XSS via map popup label; SQL injection; HTTP parameter injection | Map labels JSON-serialized via `JsonSerializer.Serialize()`; sqlite-net-pcl parameterized queries; search queries URL-encoded via `Uri.EscapeDataString`; phone regex validated at controller and transport layer; `MaxLength` on all user Entry fields |
| **A04** Insecure Design | Data loss via backup restore; alarm bypass via back button; SOS accidental send | Validate-before-clear in `BackupService`; `OnBackButtonPressed` overridden in `AlarmStageView`; SOS requires 2-second hold with timer nulled on page disappear |
| **A05** Security Misconfiguration | Cleartext traffic; ADB backup; captive-portal false-positive | `usesCleartextTraffic="false"`; `allowBackup="false"`; `NetCapability.Validated` check; `LocationTrackingService` with `Exported=false` and `ForegroundServiceType=TypeLocation` |
| **A07** Auth Failures | Biometric fail-open; single-scan rejection | `OnAuthenticationFailed` is a no-op (BiometricPrompt handles retries); null activity/executor → `false`; 60-second auth timeout via `CancellationTokenSource` |
| **A08** Data Integrity | Tampered backup; corrupted encrypted data | AES-GCM tag verified before any JSON deserialization; backup length guard (`< nonce + tag + 1` → `CryptographicException`) |
| **A10** SSRF | User-controlled URL construction | `HttpClient` base addresses hardcoded to known geocoding endpoints; Google Maps uses Android Intent with explicit package name, not an HTTP request |

### Hardening measures

| Area | Measure |
|---|---|
| **Local database** | AES-256 key from `RandomNumberGenerator`, stored in Android Keystore (`SecureStorage`). Never in `Preferences` or hardcoded. |
| **Backup encryption** | AES-256-GCM with fresh 12-byte nonce per export. GCM tag verified before JSON deserialization — tampered files rejected. |
| **Backup restore** | All records validated **before** DB clear. Contacts filtered by PH phone format + name ≤ 50 chars. Routes: name 2–30 chars, coordinates within PH bounding box. TripHistory: date range, distance ≤ 1 000 km, stage 0–3, snooze ≤ 100. Quantity caps enforced. Preferences clamped before write. |
| **Biometric auth** | `OnAuthenticationFailed` no-op; `OnAuthenticationError` → false. API 26–29 crash path on `BiometricStrong|DeviceCredential` combo avoided with API-level guard. 60-second timeout via `CancellationTokenSource`. |
| **SOS hold timer** | `EmergencyView.OnDisappearing` stops and nulls the timer — a page transition cannot silently fire SOS on a hidden page. |
| **SOS rate limiter** | 30-second cooldown in `HomeController`; phone number re-validated at `AndroidSmsService` transport layer (defense-in-depth). |
| **Map popup injection** | Destination name passed through `JsonSerializer.Serialize()` — `<`, `>`, `'` escaped as `\uXXXX`. |
| **WebView CSP** | Both map HTML templates include a strict Content-Security-Policy restricting scripts to `unpkg.com`, images to CartoDB/OSM hosts, blocking all `connect-src`. |
| **Captive portal** | `NetCapability.Validated` checked alongside `Internet` — prevents false "online" on hotel/airport WiFi. |
| **Ringer mode restore** | `_savedRingerMode` saved with `??=` before Stage 2+ override; restored in `DisableCriticalAudioAsync`. Per-ringtone `CancellationTokenSource` ensures a superseded alarm's callback does not silence a higher-priority alarm. |
| **Search cancellation** | Each `SearchDestinationAsync` call cancels the previous in-flight HTTP request — stale responses are silently dropped. |
| **Compiled Regex** | `PhoneRegex` is a static compiled `Regex` in `HomeController`, `AndroidSmsService`, and `BackupService` — no per-call regex compilation. |
| **Google Maps URI** | Coordinates formatted with `InvariantCulture`+`"F6"` — locale decimal separators cannot corrupt the URI. |
| **LocationTrackingService** | `Exported=false` + `ForegroundServiceType=TypeLocation` (required API 34+); `SecurityException` on location request → `StopSelf()`. |

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

### Release build (enables biometric auth, AOT compilation, SDK-only linker)

```bash
dotnet publish -f net9.0-android -c Release \
  -p:AndroidSigningKeyStore=path/to/keystore.jks \
  -p:AndroidSigningKeyAlias=key-alias \
  -p:AndroidSigningKeyPass=key-password \
  -p:AndroidSigningStorePass=store-password
```

The Release configuration enables:
- `RunAOTCompilation=true` — ahead-of-time compilation for faster cold start
- `AndroidLinkMode=SdkOnly` — strips unused SDK code from the APK
- `AndroidEnableProfiledAot=true` — profile-guided AOT for hot paths
- `DebugSymbols=false` / `DebugType=none` — no debug symbols in the production APK

### Run the security unit tests

```bash
dotnet test AlarmaApp.Tests/AlarmaApp.Tests.csproj
```

Tests cover (no Android/MAUI SDK required):
- Philippine phone number regex — valid/invalid boundary cases
- Philippines coordinate bounding box
- Latitude/longitude parsing with NaN and Infinity guards
- Alarm sound whitelist and normalization
- AlarmLeadMinutes clamping (1–60)
- Contact name length validation
- Backup restore caps (contacts, distance, alarm stage, snooze count)
- Snooze escalation threshold
- AES-GCM backup minimum length check

---

## Permissions required

| Permission | Reason |
|---|---|
| `ACCESS_FINE_LOCATION` | GPS tracking |
| `ACCESS_COARSE_LOCATION` | Network-assisted location |
| `ACCESS_BACKGROUND_LOCATION` | Keep tracking when the screen is off |
| `FOREGROUND_SERVICE` + `FOREGROUND_SERVICE_LOCATION` | Background location service (required API 34+ for location type) |
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

## Security audit log

| Date | Session | Changes |
|---|---|---|
| 2026-05-24 | Initial audit | SOS hold time 3s→2s; EmergencyView fake call routing fixed; snooze system added; search 3-char minimum; Start Trip card; HistoryView badges |
| 2026-05-24 | Security hardening | BackupService AES-CBC→GCM; AndroidBiometricAuthService fail-open paths closed; SOS rate limiter; phone validation; biometric timeout; coordinate validation; phone re-validation at SMS transport layer |
| 2026-05-24 | Feature cleanup | FakeCall feature removed; OnboardingView 4-slide tutorial; LaunchView splash; animation system unified |
| 2026-05-24 | Deep audit | Google Maps locale fix; ringer mode restore fix; ShowStartTripCard; captive portal fix; AlarmStageView back button; _alarmStageShowing guard; LaunchView timing fix; StopTrackingAsync independent try/catch; compiled Regex; validate-before-clear in BackupService |
| 2026-05-24 | UX hardening | SearchView 500ms debounce; OnboardingView swipe + _isAnimating guard; LaunchView PrepareForAppearance; biometric OnAuthenticationFailed no-op; API 26-29 BiometricPrompt crash fix |
| 2026-05-25 | Final audit | History tab flicker fix; LaunchView animation jank fix; Security Considerations blocks added to App.xaml.cs, HomeView, EmergencyView, LaunchView; build fix for x86_64 emulator |
| 2026-05-25 | Full verification audit | Security Considerations blocks added to all 17 remaining modules; SearchView MaxLength=200 added; csproj UseInterpreter conditioned on Debug; Release AOT settings added; security unit test project created (AlarmaApp.Tests); README comprehensively updated |
| 2026-05-25 | Flawless-execution audit | HomeView animation/tutorial ordering fix — fade-in now deferred until after tutorial redirect check to eliminate concurrent animation jank; SettingsView backup row tap targets widened to full-row Grid gestures; EmergencyView SOS timer defensive stop-before-create; README project structure corrected (launch_bg.jpg); version bumped to 1.0.0; production APK compiled |
| 2026-05-25 | Full-connectivity audit | AlarmStageActivated moved from HomeView to AppShell singleton — alarm modal now surfaces on any tab, not only when Home is active; FavoritesView route-tap now navigates to Home tab so user sees the destination card immediately; FavoritesView LastActionText feedback label added |
| 2026-05-25 | Second connectivity pass | AppShell _alarmStageShowing now set bidirectionally via Navigated event — fixes duplicate AlarmStageView push when user manually navigates "View Active Trip" and alarm fires; AlarmStageView Stop Trip button wired to code-behind OnStopTripClicked that stops tracking and exits the view (was Command-only, user was stranded after stopping) |

---

## Capstone project

Alarma is a PUP (Polytechnic University of the Philippines) BSIT 3-4 capstone project by Keith Justin S. Ababao, Kyla J. Barbin, Roje Alasdair T. Evangelista, and Pauline R. Lacanilaο.

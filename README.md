# Alarma

Alarma is an Android app for commuters in Metro Manila. It wakes you before your stop and sends an SOS if a trip goes wrong. The core features work without internet.

This is my capstone project for the BSIT program at Polytechnic University of the Philippines.

- Release: v11.0.0
- Platform: Android 8.0 and later (API 26-35)
- Stack: .NET MAUI 9, C#, MVC
- Tests: 400 xUnit tests

## Contents

1. [Overview](#overview)
2. [Features](#features)
3. [Security and data protection](#security-and-data-protection)
4. [Testing](#testing)
5. [Architecture](#architecture)
6. [Build and install](#build-and-install)
7. [Emulator automation](#emulator-automation)
8. [Appendix A: OWASP alignment](#appendix-a-owasp-alignment)
9. [Appendix B: Privacy and legal](#appendix-b-privacy-and-legal)
10. [Appendix C: Reliability and UX fixes](#appendix-c-reliability-and-ux-fixes)

## Overview

People fall asleep on jeepneys, UV Express vans, and city buses and miss their stop. That is worse at night, and it can put them in a bad spot. Most alarm apps only count down time, and navigation apps need you to keep watching them. Neither helps a passenger who wants to rest.

Alarma tracks the trip by GPS, measures the distance to the destination, and escalates an alarm in three stages as the vehicle gets closer. The trigger distance changes with the vehicle's speed. If the vehicle passes the stop, the app shows a wake-up alert and offers a reroute. Holding the SOS button sends the rider's live location to emergency contacts through a normal background SMS, even with no data connection.

All personal data stays on the phone. Trip history, saved routes, and emergency contacts are stored in an encrypted local database, following the Data Privacy Act of 2012 (Republic Act No. 10173). There is no Alarma backend and no first-party server receiving personal data.

The current build is the Release build: signed, trimmed, AOT-compiled, and installed on physical devices for field testing. It has been checked against the OWASP Mobile Top 10 and covered by the test suite described below.

## Features

### Three-stage adaptive alarm

Instead of one alarm at one distance, Alarma uses three stages. The lead radius is based on a rolling average speed and a reaction window, capped at 5 km. A bus on the highway triggers the alarm earlier than a jeepney crawling through traffic.

| Stage | Trigger | What happens |
|---|---|---|
| 1: Gentle alert | At the adaptive lead radius | One soft vibration. No full-screen takeover and no forced audio. |
| 2: Escalated alert | Around 50% of the lead radius | Stronger vibration and the selected alarm sound at raised volume. |
| 3: Emergency lockout | At the drop-off point | Full-screen takeover, maximum alarm-channel volume, and continuous maximum vibration. Only the Slide to Stop gesture clears it. |

Audio and vibration run on a background `Task.Run` thread, so the screen and the slide gesture do not freeze.

The app ships with five alarm sounds in `res/raw`: Digital Clock, Siren, Buzzer, Bell, and Air Horn. They play through the Android alarm channel (`AudioUsageKind.Alarm` / `STREAM_ALARM`), so the volume is the same on every phone and does not depend on the ringtone or media volume.

Riders can also import their own sound (MP3, OGG, WAV, M4A, or AAC) with the native file picker. The app checks the file type, the 10 MB size limit, and whether the file can actually play. A rejected file gets a grey-pill message. A valid file is copied into app-private storage and listed as a Custom option. It uses the same alarm channel as the bundled sounds. The choice survives backup and restore, and a deleted custom file falls back to the default instead of leaving the alarm silent.

Stage 2 and the emergency stage can each have their own sound, including None. Stage 1 is vibration-only on purpose, so it has no sound picker. If a stage has no explicit sound, it uses the shared sound. Each stage selector has a preview button.

If the vehicle passes the destination, Alarma detects it from consecutive increasing-distance fixes rather than a single noisy reading. It then shows a full-screen alert with the distance overshot, an offline Area Safety overlay, and an in-app reroute screen with a mini-map and step-by-step return guidance. There is also a one-tap hand-off to Google Maps through a local `google.navigation:q={lat},{lon}` intent, with no network request. The overshoot distance is adjustable from 50 to 500 m in Settings, default 250 m, and is clamped on input and restore.

### Location tracking

- GPS tracking runs in a foreground service (`LocationTrackingService`) with `Exported=false` and `ForegroundServiceType=TypeLocation`. No other app can start or bind it.
- Trip distance is a Haversine path integral between fixes. A jitter gate drops movement smaller than the fix's own accuracy, with an 8 m floor, so a phone sitting still does not accumulate distance.
- In dense areas, three on-device checks keep bad fixes from faking an arrival. An accuracy-weighted smoother prefers the better of the current estimate and the new fix. A fix below the confidence threshold can update the displayed distance but cannot trigger arrival, overshoot, or the stage-3 lockout. The jitter gate widens with the reported accuracy so a bad fix cannot inflate the distance.
- A Leaflet map with CartoDB dark tiles runs under a strict Content Security Policy and shows the interpolated location pin and destination marker.
- A `PARTIAL_WAKE_LOCK` is held only while location providers are registered, which stops aggressive OEM power managers from throttling updates.

### SOS

- Holding the SOS button for 2 seconds sends the message in the background through `Android.Telephony.SmsManager`, including multipart messages. The rider does not have to open the messaging app. `SEND_SMS` is requested in context right before the first send, and the delivery `PendingIntent` uses `FLAG_IMMUTABLE` for Android 12 and later.
- The message has no URL because carriers often block links. With a connection, Alarma reverse-geocodes the live location and sends a street address: "EMERGENCY! I need help near {address}." Without a connection, it sends coordinates that a contact can paste into a map app.
- Each contact is messaged in its own `try/catch`, so one bad number does not stop the rest. A 30-second cooldown and a single-fire latch prevent repeat sends. If location is off, dispatch stops and shows a prompt. A short haptic buzz and beep confirm the press without drawing attention.
- The Call 911 button uses `Launcher.OpenAsync("tel:911")`, resolved through a `<queries>` `DIAL`/`tel` manifest entry so Android 11+ package visibility does not hide the dialer. If nothing resolves, a grey-pill message appears.

### Bluetooth

- One native `BroadcastReceiver` listens for `ACTION_STATE_CHANGED`, `ACTION_ACL_CONNECTED`, and `ACTION_ACL_DISCONNECTED`.
- The Settings toggle follows the physical radio state. Plugging in or removing earphones shows an Earphones Connected or Earphones Disconnected message.
- The receiver does not read device details and does not request `BLUETOOTH_CONNECT`. It checks the audio output state to filter out non-audio devices.

## Security and data protection

The app holds location data and emergency contacts, so the threat model assumes someone has physical or filesystem access to a lost, stolen, or rooted phone. Appendix A maps the controls to OWASP categories.

### Encrypted backups (.alarma)

`Services/BackupService.cs` creates portable, authenticated, encrypted backups.

- AES-256-GCM with a new 96-bit nonce for every export. File layout: `[1-byte version][12-byte nonce][16-byte GCM tag][ciphertext]`.
- The key is a fixed 256-bit value built into the app (the SHA-256 of a constant), so every install uses the same key. Export and restore need no password, and a backup opens on any device and survives uninstall and reinstall. This replaces the earlier random Keystore key, which was unique per install and wiped on uninstall, so old backups looked damaged after a reinstall or on a new phone. The static key is a deliberate trade-off for portability and demos, not a password-derived design.
- The backup is not confidential from someone who has the app. Because the key ships in the app, anyone with a copy of Alarma can decrypt the file. The envelope gives integrity, not secrecy. Store exported files somewhere you trust, like any other personal export. The on-device database uses a separate per-device Keystore key.
- Export opens the system Save As dialog (`FileSaver`) so the user picks the folder, usually Downloads. Import opens the native file picker. Only the file location moves, not the format.
- Restore validates every record before touching the live database, so a tampered or empty backup cannot wipe real data. Fields are length-capped, phone numbers are checked against the Philippine format, coordinates are bounded to the Philippines, and quantity limits match the app.
- The GCM tag is checked before any JSON is deserialized. A corrupted or modified file, or one with an unknown version, is rejected before any plaintext is read.

### Local database

`Services/DatabaseService.cs` stores trip history, saved routes, emergency contacts, the geocode cache, and behavior profiles in SQLite encrypted with SQLCipher (AES-256). The key is generated with `RandomNumberGenerator` and kept in the Android Keystore under `alarma_db_key_v1`; it is never written to `Preferences`. Access uses `sqlite-net-pcl` parameterized queries, with no raw SQL. `android:allowBackup="false"` blocks ADB backup extraction.

### Crash and fault logging

`Services/BlackBoxLogger.cs` encrypts crashes and handled exceptions with AES-256-GCM into a device-local log, using a key in the Keystore (`alarma_blackbox_key_v2`). This matters when a fault happens in the field and no debugger or ADB is available. Two global handlers are installed in `MauiProgram.cs` before app code runs. The previous crash is decrypted into a readable report on the next launch, including in a Release build. The key is preloaded so the terminating-process handler can encrypt synchronously without a crash loop.

### Offline operation

Tracking, alarms, saved routes, and SOS work without internet. Coordinates are held in memory as immutable value objects and are not logged or persisted by the tracking layer. The only network use is destination search and map tiles over HTTPS, using open services with no API keys and no personal identifiers.

## Testing

The test suite has 400 passing xUnit tests and gates each release.

- Hardware dependencies that a CI machine cannot exercise are replaced with Moq mocks: the reverse geocoder (`IGeocoding`), the Bluetooth receiver, and the earphone audio probe. The C# decision logic runs deterministically and offline, with no radio, SIM, or GPS.
- Coverage includes:
  - SOS formatting and geocoding, including the online address format and the offline coordinate fallback.
  - The alarm state machine, including stage transitions, monotonic escalation, and the stage-3 latch under GPS noise.
  - Adaptive alarm math: rolling average speed, reaction window, and stage boundaries.
  - Backup and restore: the GCM envelope round-trips, tampered files and wrong keys are rejected, and validation strips bad records.
  - Bluetooth UI sync from simulated broadcasts, including the connected and disconnected messages and `PropertyChanged`.
  - Custom and per-stage sounds: whitelist normalization, import validation, fallback for a deleted custom file, and per-stage inheritance.
  - Overshoot distance: the 50-500 m clamp on input and restore, threshold math, and the latch under tight and wide buffers.
  - Crowded-place GPS: low-accuracy and scattered fixes do not cause a false arrival or overshoot, smoothing works, and jitter does not inflate distance.
  - Backup round-trip for new settings, including per-stage choices, the custom sound reference, and overshoot distance. Older backups still load.
  - Phone number validation, coordinate bounds, sound whitelisting, lead-time clamping, timezone formatting, and the geocode cache LRU.
- `AlarmaApp.Tests` targets plain `net9.0` with no Android or MAUI dependency, so `dotnet test` runs anywhere.

```bash
dotnet test AlarmaApp.Tests/AlarmaApp.Tests.csproj
# Passed: 400, Failed: 0, Skipped: 0
```

## Architecture

The app uses MVC. XAML views bind to `HomeController`, which calls injected services such as `ILocationService`, `ISmsService`, `IBluetoothMonitor`, and `IGeocoding`. Android-specific code lives under `Platforms/Android`, and dependency injection is set up in `MauiProgram.cs`, which is what makes the logic testable with Moq.

| Layer | Technology |
|---|---|
| Framework | .NET MAUI (.NET 9) |
| Language | C# |
| Architecture | MVC with DI services |
| Target framework | `net9.0-android` |
| Local database | `sqlite-net-pcl` over SQLCipher (AES-256) |
| Key-value store | MAUI `Preferences` for non-sensitive settings |
| Secrets | Android Keystore through MAUI `SecureStorage` |
| Map | Leaflet.js with CartoDB dark tiles in a MAUI `WebView` under a strict CSP |
| Geocoding | Photon first, Nominatim for street-level results over OSM, then the device geocoder for subdivisions that OSM does not map, plus a Philippine alias dictionary and a coordinate label fallback |
| Cryptography | `System.Security.Cryptography`, `AesGcm`, `RandomNumberGenerator` |
| Testing | xUnit and Moq, 400 tests on `net9.0` |
| Icons | Google Material Symbols font ligatures |

### Target platform

| Property | Value |
|---|---|
| Platform | Android, portrait, 5.0-6.7 inch phones |
| Minimum OS | Android 8.0 (API 26) |
| Target OS | Android 15 (API 35) |
| Application ID | `com.alarma.app` |
| Release | v11.0.0 |
| Out of scope | iOS, web, desktop |

### Release hardening

The Release configuration in `AlarmaApp.csproj` also reduces the reverse-engineering surface.

| Property | Value | Effect |
|---|---|---|
| `RunAOTCompilation` | `true` | Managed IL is compiled to native code, so there is no IL to decompile. |
| `AndroidEnableProfiledAot` | `true` | Profile-guided AOT prioritizes the alarm trigger and GPS callback for faster cold starts. |
| `PublishTrimmed` / `TrimMode` | `true` / `full` | The trimmer removes unreferenced code across the dependency closure. |
| `AndroidLinkMode` | `SdkOnly` | The Android linker strips unused SDK and AndroidX code at the DEX level. |
| `DebugSymbols` / `DebugType` | `false` / `none` | No symbols or sequence points, so decompilers cannot map native code to source lines. |

The trimmer cannot see reflection, so `TrimmerRoots.xml` roots the `AlarmaApp.Models` namespace and the SQLite ORM assemblies.

## Build and install

These steps install the Release build on a physical Android device without Google Play.

### Prerequisites

- .NET 9 SDK
- MAUI Android workload: `dotnet workload install maui-android`
- Android SDK Platform Tools, with `adb` on the `PATH`
- A phone running Android 8.0 (API 26) or later

### Build the APK

From the repository root:

```bash
dotnet publish AlarmaApp.csproj -f net9.0-android -c Release -p:AndroidPackageFormat=apk
```

The `-p:AndroidPackageFormat=apk` flag overrides the default `.aab` output and produces an installable APK:

```
bin/Release/net9.0-android/publish/com.alarma.app-Signed.apk
```

### Install with ADB

```bash
adb install -r "bin/Release/net9.0-android/publish/com.alarma.app-Signed.apk"
```

The `-r` flag reinstalls over an existing copy and keeps data. To install manually, copy the APK to the phone, open it with a file manager, allow Install unknown apps, and confirm.

### First run

The first launch opens a short onboarding flow. Each slide is a full-bleed illustration with Skip and Next as transparent tap zones over the artwork, so taps and the system click sound only fire on the buttons. The last slide opens a consent popup with the Privacy Policy and Terms and Conditions. The Get Started button stays disabled until the agreement box is ticked. `HomeView` enforces this, so onboarding and consent cannot be skipped.

The app then asks for location (including background), notifications, and SMS permissions, and offers a battery optimization exemption. Each permission is explained in context because location and SMS are required for the alarm and SOS features.

## Emulator automation

`AutomationTools/simulate_commute.py` drives the commute and alarm flow on an Android emulator through ADB, so the stages can be tested without moving. The route follows the University of Santo Tomas corridor from south to north, passing UST (arrival) and continuing past it (overshoot), which fires all three stages.

```bash
# 1. Grant permissions on the emulator
adb shell pm grant com.alarma.app android.permission.ACCESS_FINE_LOCATION
adb shell pm grant com.alarma.app android.permission.ACCESS_COARSE_LOCATION
adb shell pm grant com.alarma.app android.permission.POST_NOTIFICATIONS

# 2. Run the full flow: launch, search, select, start, 28 GPS fixes
python AutomationTools/simulate_commute.py

# 3. Crowded-place run with scattered low-confidence fixes
python AutomationTools/simulate_commute.py --scenario crowded   # optional: --noise 60 --seed 1337
```

GPS is injected through the emulator console (`adb emu geo fix`). A physical phone needs a mock-location provider. Tap coordinates are set for a 1080x2400 emulator and are editable constants at the top of the script. The `--scenario crowded` mode shifts each fix by up to a configurable radius (60 m by default, deterministic per seed) to imitate multipath in a dense city, which exercises the smoothing, confidence gate, and jitter gate end to end.

## Appendix A: OWASP alignment

| OWASP category | Control in Alarma |
|---|---|
| A01 Broken access control | Onboarding and permission gates run before initialization. Data is device-local with no cross-user surface. The tracking service is `Exported=false`. |
| A02 Cryptographic failures | AES-256-GCM for backups and the black-box log, SQLCipher AES-256 for the database. Keys are random and Keystore-resident, with no hardcoded keys. |
| A03 Injection | Parameterized SQL throughout. Map labels go through `JsonSerializer.Serialize`, search input is URL-encoded, and phone numbers are regex-validated at the controller and transport layers. |
| A04 Insecure design | Restore validates before clearing data. SOS requires a 2-second hold, the timer clears on page exit, and a 30-second cooldown applies. |
| A05 Security misconfiguration | `usesCleartextTraffic="false"`, `allowBackup="false"`, captive-portal detection, and a hardened foreground service. |
| A08 Software and data integrity failures | GCM tags are checked before deserialization for both backups and black-box records. |
| A09 Security logging failures | Encrypted, Keystore-backed logging stays readable in Release. Handled exceptions go to the logger instead of being dropped. |
| A10 Server-side request forgery | Geocoding base addresses are hardcoded. The reroute uses an explicit Android intent, not an HTTP request. |

## Appendix B: Privacy and legal

Personal data stays on the phone. The exceptions are destination text queries and map-tile requests to CartoDB and OpenStreetMap, all over HTTPS and without personal identifiers. Destination queries go to Photon and Nominatim. If neither returns a match, usually an OSM-unmapped subdivision, the query text goes to the device's platform geocoder (Google's `Geocoder` on Android). Only the query text is sent; no coordinates, contacts, or identifiers go with it. SOS messages go through Android `SmsManager` over the cellular network and do not pass through an Alarma server. GPS coordinates, emergency contacts, trip history, and behavior data stay in the encrypted local database, following the Data Privacy Act of 2012 (Republic Act No. 10173). The full Privacy Policy and Terms and Conditions are under Settings > Legal.

## Appendix C: Reliability and UX fixes

A functional and UX audit before the panel defense fixed issues found in pre-defense testing. The security work in the previous section was left unchanged. The fixes include: requiring master GPS before a trip starts; two-way permission toggles that open App Settings; the Haversine distance refactor with a jitter gate; a full state reset on stop so no ghost trip remains; monotonic, outlier-resistant stage escalation; notification deep links into the active trip through an explicit `PendingIntent`; and grey-pill toast messages instead of blocking modals.

Release notes are in [CHANGELOG.md](CHANGELOG.md).

# Alarma — Adaptive Anti-Oversleep Destination Alarm & Emergency Safety System

> An **offline-first** Android safety application for Metro Manila public-transport commuters.
> Capstone & Application-Development Project — Bachelor of Science in Information Technology,
> Polytechnic University of the Philippines.
>
> **Release:** v10.0.0 · **Platform:** Android 8.0+ (API 26–35) ·
> **Stack:** .NET MAUI 9 / C# / MVC · **Quality gate:** 268 passing xUnit tests.

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [Core Features](#2-core-features)
3. [Security & Data Protection](#3-security--data-protection)
4. [Testing & Quality Assurance](#4-testing--quality-assurance)
5. [Architecture & Technology Stack](#5-architecture--technology-stack)
6. [Build, Installation & Sideloading](#6-build-installation--sideloading)
7. [Emulator Automation & Field Telemetry](#7-emulator-automation--field-telemetry)
8. [Appendix A — OWASP Alignment](#appendix-a--owasp-alignment)
9. [Appendix B — Privacy & Legal](#appendix-b--privacy--legal)
10. [Appendix C — Reliability & UX Audit Resolution](#appendix-c--reliability--ux-audit-resolution)

---

## 1. Executive Summary

Alarma addresses a concrete, recurring risk for Metro Manila commuters: falling asleep aboard a
jeepney, UV Express, or city bus and missing the intended stop — often late at night, and frequently
with personal-safety consequences. Existing alarm and navigation tools are time-based or demand
constant attention; neither suits a passenger who needs to rest during a long commute.

At its core, Alarma is an **adaptive commuter-safety tool built around progressive alarm staging**.
It tracks the commute by GPS, computes the distance to the chosen destination, and escalates an alarm
through three progressively stronger stages — adapting the trigger distance to the vehicle's speed — as
the passenger nears the stop. If the vehicle overshoots, Alarma raises a wake-up alert and offers a
one-tap reroute. A press-and-hold **emergency SOS** sends the passenger's live, geocoded location to
designated contacts over **true background cellular SMS**, functioning even without internet.

The application is built **offline-first** and **privacy-first**: all personal data — trip history,
saved routes, emergency contacts — is stored only on the device, encrypted at rest, in keeping with
Republic Act No. 10173 (Data Privacy Act of 2012). There is no proprietary backend, and no personal
data is transmitted to any first-party server.

**Status — Production-ready.** Audited against the OWASP Mobile / Top-10 categories, covered by an
automated **268-test** xUnit suite, and compiled to a signed, trimmed, ahead-of-time-compiled Release
APK for physical field testing.

---

## 2. Core Features

### 2.1 Progressive 3-Stage Adaptive Alarm — with 5 high-intensity audio options

Rather than a single binary alarm, Alarma escalates in three distance-aware stages so a drowsy
commuter is woken with the least force necessary yet never sleeps through the stop. Trigger distances
are a **speed-based adaptive lead radius** (a rolling-average-speed × reaction-window calculation,
capped at 5 km), so the alarm arms earlier on a fast highway bus and later in crawling city traffic.

| Stage | Trigger | Behaviour |
|---|---|---|
| **Stage 1 — Gentle Alert** | At the adaptive lead radius | A single soft vibration nudge. No screen takeover, no forced audio. |
| **Stage 2 — Escalated Alert** | ~50% of the lead radius | Stronger vibration pattern **and** the chosen alarm sound at raised volume. |
| **Emergency Stage — Full-Screen Lockout** | At the drop-off coordinate | Full-screen takeover, **maximum** alarm-channel volume, and **continuous** maximum-intensity vibration that only the physical **Slide to Stop** gesture can clear. |

All escalation audio/vibration runs on a background `Task.Run` thread, so neither the UI nor the slide
gesture ever freezes.

**Five high-intensity, bundled alarm voices.** Every option is a real audio asset shipped in
`res/raw` — not a device-dependent system tone — and is pinned to the native Android **alarm channel**
(`AudioUsageKind.Alarm` / `STREAM_ALARM`), so it plays at maximum alarm volume identically on any
handset:

- **Digital Clock** — the classic loud, rhythmic beep-beep-beep wake-up alarm.
- **Siren** — a high-pitched emergency sweep.
- **Buzzer** — a heavy-duty industrial buzz.
- **Bell** — a sharp mechanical school/fire-alarm ring.
- **Air Horn** — a deep, piercing blast.

**Overshoot recovery with Google Maps hand-off.** If the vehicle passes the destination, Alarma
detects it from **consecutive increasing-distance fixes** (never a single noisy reading) and walks the
rider through a guided recovery: a confirmed full-screen alert (destination + exact distance overshot)
→ an offline *Area Safety* guidance overlay → an in-app rerouting screen with a mini-map and on-device,
step-by-step return guidance, plus a one-tap hand-off to Google Maps via a pure local
`google.navigation:q={lat},{lon}` Intent (**zero network requests**).

### 2.2 Live Location Tracking

- Continuous GPS tracking runs in an Android foreground `Service` (`LocationTrackingService`) declared
  `Exported=false` with `ForegroundServiceType=TypeLocation` (required from API 34), so no other app can
  start or bind it.
- Trip distance is accumulated as a true **Haversine path integral** between fixes, with a per-segment
  GPS-jitter gate (movement below the fix's own accuracy, floored at 8 m, is discarded) so a stationary
  phone's GPS wander never inflates distance.
- A live Leaflet map (CartoDB dark tiles under a strict Content-Security-Policy) shows a smoothly
  interpolated location pin and the destination marker.
- A `PARTIAL_WAKE_LOCK` is held only while providers are registered and released on stop, preventing
  aggressive OEM power managers from throttling location delivery.

### 2.3 Offline-Safe SOS — true background SMS with geocoded addresses

- **True background dispatch.** A 2-second press-and-hold fires the SOS, which sends instantly in the
  background via `Android.Telephony.SmsManager` (multipart-aware). The rider never has to open the
  messaging app and tap *Send*. `SEND_SMS` is requested natively, in context, immediately before the
  first send, and the delivery `PendingIntent` is `FLAG_IMMUTABLE` for Android 12+ compliance.
- **Human-readable, URL-free body.** Carriers routinely block link-laden SMS, so the message carries no
  URL. Online, Alarma reverse-geocodes the rider's **live** location into a real street address —
  *"EMERGENCY! I need help near {address}."* Offline, it falls back to plain coordinates a contact can
  paste into any map app.
- **Resilient by design.** Each contact is messaged in its own `try/catch` (one bad number can't abort
  the rest); a 30-second cooldown and a single-fire latch prevent accidental spam; and dispatch halts
  with a prompt if device location is switched off. A discreet haptic buzz + brief beep confirms the
  press without broadcasting it — important when the user may be vulnerable.
- **Bulletproof 911 hand-off.** *Call 911* uses `Launcher.OpenAsync("tel:911")`, resolved through a
  `<queries>` `DIAL`/`tel` manifest declaration so Android 11+ package-visibility can't hide the dialer;
  if none resolves, a transient grey-pill notifies the user instead of failing silently.

### 2.4 Native Android Bluetooth Syncing

- A single native `BroadcastReceiver` listens for the adapter's `ACTION_STATE_CHANGED` **and** for
  device `ACTION_ACL_CONNECTED` / `ACTION_ACL_DISCONNECTED`.
- The in-app Settings Bluetooth control mirrors the **physical radio in real time**, and plugging
  earbuds in or out flashes a transient *Earphones Connected* / *Earphones Disconnected* grey-pill.
- Device details are never read (no `EXTRA_DEVICE`), so the feature needs no `BLUETOOTH_CONNECT`
  runtime permission; the receiver re-queries the real audio-output state itself to filter out
  non-audio devices.

---

## 3. Security & Data Protection

Security was treated as a primary design constraint, not an afterthought. Because the app holds
location data and emergency contacts, the threat model assumes an attacker with physical or filesystem
access to a lost, stolen, or rooted device. A consolidated control-to-OWASP mapping is in
[Appendix A](#appendix-a--owasp-alignment).

### 3.1 AES-256-GCM Encrypted `.alarma` Backups — portable, password-derived keys

`Services/BackupService.cs` produces portable, authenticated-encrypted backups of user data.

- **AES-256-GCM** authenticated encryption with a **fresh 96-bit nonce per export**.
  File layout: `[1-byte version][16-byte salt][4-byte PBKDF2 iterations][12-byte nonce][16-byte GCM tag][ciphertext]`.
- **Password-derived, portable key.** The 256-bit key is derived from a user-supplied password via
  **PBKDF2-HMAC-SHA256** (210,000 iterations) using a random per-file salt. The salt and iteration count
  travel inside the file, so the key is reproducible on any device from `(password + file)` alone — there
  is **no stored key**. This fixes the prior design, where a random key in the **Android Keystore
  (`SecureStorage`)** was wiped on uninstall and was unique per install, so backups read as "damaged"
  after a reinstall or on a new phone. A backup exported on Phone A now restores on a fresh install of
  Phone B given the same password; a wrong password fails the GCM tag and is rejected before any data is touched.
- **User-owned storage.** Exporting opens the native OS "Save As" dialog (`FileSaver`) so the user
  picks the destination folder — typically **Downloads** — and importing opens the native file browser
  (`FilePicker`) to select the `.alarma` file back. The encryption envelope is identical either way;
  only the file's *location* moves out of hidden app storage into a folder the user controls.
- **Validate-before-clear restore.** Every record is validated before the live database is touched, so a
  tampered or empty backup can never silently wipe real data. Restored fields are length-capped, phone
  numbers are re-validated against the Philippine format, coordinates are bounded to the Philippine
  envelope, and quantity caps mirror the in-app limits.
- **Integrity enforcement.** The GCM authentication tag is verified before any JSON is deserialized;
  corrupted or modified files are rejected before a single plaintext byte is read.

### 3.2 Encrypted Local Database — SQLCipher (AES-256)

`Services/DatabaseService.cs` persists all structured data (trip history, saved routes, emergency
contacts, geocode cache, behavioral profiles) in a SQLite database encrypted at rest with **SQLCipher
(AES-256)**. The key is `RandomNumberGenerator`-generated and Keystore-resident (`alarma_db_key_v1`),
never written to `Preferences`. All access uses `sqlite-net-pcl` **parameterized queries** (no raw SQL,
no injection surface), and `android:allowBackup="false"` blocks extraction via ADB backup.

### 3.3 Forensic Black-Box Logger — AES-256-GCM, Android Keystore

`Services/BlackBoxLogger.cs` seals crashes and handled exceptions with **AES-256-GCM** into a
device-local encrypted log (`alarma_blackbox_key_v2`) so field faults — where a debugger and ADB are
unavailable — can still be recovered. Two global handlers are installed in `MauiProgram.cs` before any
app code runs; the prior crash is decrypted into a readable report on next launch, fully operational in
a Release build. The key is preloaded so the terminating-process handler can encrypt synchronously
without a crash-on-crash loop.

### 3.4 Privacy-First, Offline Operation

Tracking, alarms, saved routes, and SOS all function **without internet**. Coordinates are held
in-memory as immutable value objects and **never logged or persisted to disk** by the tracking layer.
The only network use is destination text search and map tiles, over HTTPS to open services with no API
keys and no personal identifiers.

---

## 4. Testing & Quality Assurance

Alarma ships with a robust, fully automated **xUnit** test suite — **268 passing tests** — that gates
every release.

- **Hardware safely isolated with Moq.** Native MAUI/Android dependencies that a CI machine cannot
  physically exercise — the reverse geocoder (`IGeocoding`), the Bluetooth broadcast listener, and the
  earphone audio probe — are replaced with **Moq** mocks behind test-local interfaces. This lets the
  full C# decision logic be tested rigorously, deterministically, and offline, with no real radio, SIM,
  or GPS required.
- **Comprehensive coverage of the v7 feature set:**
  - **SOS formatter & geocoding** — a mocked geocoding source proves the online *"…near {address}."*
    format and the offline coordinate-fallback path (URL-free, culture-invariant).
  - **Progressive alarm state machine** — Stage None → 1 → 2 → 3 transitions fire strictly on the
    distance thresholds, escalate monotonically, and latch (the Stage-3 lockout never downgrades on a
    GPS wobble).
  - **Adaptive alarm mathematics** — rolling-average speed, the adaptive reaction window, and the
    speed-based stage boundaries, asserted against the technical specification's worked examples.
  - **Backup/restore serialization** — the AES-256-GCM `.alarma` envelope round-trips a profile
    exactly, rejects any tamper (`AuthenticationTagMismatchException`) or wrong key, and the
    validate-before-restore filters strip junk records.
  - **Bluetooth UI sync** — simulated hardware broadcasts drive the ViewModel's UI state
    (toggle/label and the *Earphones Connected/Disconnected* pill), with `PropertyChanged` verified.
  - Plus phone-number validation, coordinate bounding, alarm-sound whitelisting, lead-time clamping,
    timezone formatting, and geocode-cache LRU behaviour.
- **Clean separation.** The suite (`AlarmaApp.Tests`) targets plain `net9.0` with no Android/MAUI
  dependency, so it runs anywhere `dotnet test` runs. Production code is never modified to accommodate a
  test.

```bash
dotnet test AlarmaApp.Tests/AlarmaApp.Tests.csproj
# Passed!  - Failed: 0, Passed: 268, Skipped: 0
```

---

## 5. Architecture & Technology Stack

Alarma follows a clean **Model–View–Controller (MVC)** separation: XAML `Views` bind to a
`HomeController` view-model, which orchestrates injected, interface-backed services
(`ILocationService`, `ISmsService`, `IBluetoothMonitor`, `IGeocoding`, …). Native behaviour lives in
platform implementations under `Platforms/Android`, and dependency injection is configured in
`MauiProgram.cs` — the seam that makes the logic unit-testable with Moq.

| Layer | Technology |
|---|---|
| Framework | .NET MAUI (.NET 9) |
| Language | C# |
| Architecture | Model–View–Controller (MVC) with DI-injected services |
| Target framework | `net9.0-android` |
| Local database | `sqlite-net-pcl` over SQLCipher (AES-256) |
| Key–value store | MAUI `Preferences` (non-sensitive settings only) |
| Secrets | Android Keystore via MAUI `SecureStorage` |
| Mapping | Leaflet.js with CartoDB dark tiles in a MAUI `WebView` under a strict CSP |
| Geocoding | Three-tier forward search — Photon (primary) + Nominatim (street-level) over OSM, then the device's native `Geocoder` for OSM-unmapped subdivisions — plus a PH alias dictionary and a coordinate-label fallback |
| Cryptography | `System.Security.Cryptography` — `AesGcm`, `RandomNumberGenerator` |
| Testing | xUnit + **Moq** (hardware isolation), 268 tests on `net9.0` |
| Iconography | Google Material Symbols font ligatures (`MaterialSymbolsOutlined.ttf`) |

### Target platform

| Property | Value |
|---|---|
| Platform | Android (portrait, 5.0–6.7-inch smartphones) |
| Minimum OS | Android 8.0 (API 26) |
| Target OS | Android 15 (API 35) |
| Application ID | `com.alarma.app` |
| Release | v10.0.0 |
| Out of scope | iOS, web, desktop |

### Release hardening pipeline

The Release configuration in `AlarmaApp.csproj` applies layered hardening that also reduces
reverse-engineering surface:

| Property | Value | Effect |
|---|---|---|
| `RunAOTCompilation` | `true` | Managed IL is AOT-compiled to native code; no IL remains to decompile. |
| `AndroidEnableProfiledAot` | `true` | Profile-guided AOT prioritizes hot paths (alarm trigger, GPS callback) for faster cold start. |
| `PublishTrimmed` / `TrimMode` | `true` / `full` | The IL trimmer removes unreferenced code across the full dependency closure. |
| `AndroidLinkMode` | `SdkOnly` | The Android linker strips unused SDK/AndroidX code at the DEX level. |
| `DebugSymbols` / `DebugType` | `false` / `none` | No symbols or sequence points; decompilers cannot map native code to source lines. |

Because the trimmer cannot observe reflection, `TrimmerRoots.xml` roots the `AlarmaApp.Models`
namespace and the SQLite ORM assemblies so reflection-instantiated types survive trimming.

---

## 6. Build, Installation & Sideloading

These instructions assume an evaluator with the source repository who wishes to install the compiled
Release build on a physical Android device for field testing, without Google Play.

### 6.1 Prerequisites

- .NET 9 SDK
- MAUI Android workload: `dotnet workload install maui-android`
- Android SDK Platform Tools (provides `adb`) on the `PATH`
- A physical Android device running Android 8.0 (API 26) or later

### 6.2 Produce the sideload-ready APK

From the repository root:

```bash
dotnet publish AlarmaApp.csproj -f net9.0-android -c Release -p:AndroidPackageFormat=apk
```

The `-p:AndroidPackageFormat=apk` flag overrides the default Android App Bundle (`.aab`) and emits a
self-contained, installable `.apk`. The signed, sideload-ready artifact is:

```
bin/Release/net9.0-android/publish/com.alarma.app-Signed.apk
```

### 6.3 Install over USB with ADB (recommended)

```bash
adb install -r "bin/Release/net9.0-android/publish/com.alarma.app-Signed.apk"
```

The `-r` flag reinstalls over an existing copy while preserving data. **Manual install:** transfer the
APK to the device, open it with a file manager (with *Install unknown apps* enabled), and confirm.

### 6.4 First-run onboarding & permissions

On first launch the app opens a short, swipe-free onboarding flow. Each slide is a single full-bleed
illustration mapped one-to-one from the design mockups, with Skip and Next as transparent tap zones over
the artwork — so navigation (and the system click sound) fires only on the buttons, never across the
screen. The final slide raises a consent popup linking the **Privacy Policy** and **Terms & Conditions**;
the *Get Started* button stays inert until the agreement checkbox is ticked. `HomeView` enforces this as
a gate — onboarding and the legal consent cannot be skipped before the app initializes.

Immediately after, the app guides the user through granting location (including background),
notifications, and SMS permissions, and offers a battery-optimization exemption. All permissions are
explained in context; location and SMS are central to the destination-alarm and emergency-SOS features.

---

## 7. Emulator Automation & Field Telemetry

The commute and alarm pipeline is exercised on an Android emulator with a hands-free ADB script,
`AutomationTools/simulate_commute.py`, so the multi-stage alarm can be verified end-to-end without
physically moving. The script models a real Metro Manila commute along the **University of Santo
Tomas** corridor — a straight south-to-north line passing through UST (Arrival) and continuing past it
(Overshoot) so all three alarm stages fire.

```bash
# 1. Bypass first-run permission dialogs (emulator only)
adb shell pm grant com.alarma.app android.permission.ACCESS_FINE_LOCATION
adb shell pm grant com.alarma.app android.permission.ACCESS_COARSE_LOCATION
adb shell pm grant com.alarma.app android.permission.POST_NOTIFICATIONS

# 2. Drive the full hands-free run (launch → search → select → start → 28 GPS fixes)
python AutomationTools/simulate_commute.py
```

GPS injection uses the emulator console bridge (`adb emu geo fix`); a physical handset needs a
mock-location provider. Tap coordinates are calibrated for a 1080×2400 emulator and exposed as editable
constants at the top of the script.

---

## Appendix A — OWASP Alignment

| OWASP category | Control in Alarma |
|---|---|
| A01 Broken Access Control | Onboarding and permission gates enforced before initialization; device-local data with no cross-user surface; tracking service `Exported=false`. |
| A02 Cryptographic Failures | AES-256-GCM (backup, black box) and SQLCipher AES-256 (database); all keys random and Keystore-resident; no hardcoded keys. |
| A03 Injection | Parameterized SQL throughout; map labels passed through `JsonSerializer.Serialize`; search input URL-encoded; phone numbers regex-validated at controller and transport layers. |
| A04 Insecure Design | Validate-before-clear backup restore; SOS 2-second hold with timer cleared on page exit; 30-second SOS cooldown. |
| A05 Security Misconfiguration | `usesCleartextTraffic="false"`; `allowBackup="false"`; captive-portal detection; foreground-service hardening. |
| A08 Software & Data Integrity Failures | AES-GCM authentication tags verified before deserialization for both backup and black-box records. |
| A09 Security Logging Failures | Encrypted, Keystore-backed forensic logging that remains readable in Release; handled exceptions routed to the logger rather than discarded. |
| A10 Server-Side Request Forgery | Geocoding base addresses hardcoded; map reroute uses an explicit Android Intent, not an HTTP request. |

## Appendix B — Privacy & Legal

No personal data leaves the device except destination text queries and map-tile requests (to
CartoDB/OpenStreetMap), all over HTTPS and free of personal identifiers. Destination text queries are
sent to Photon and Nominatim; when both return no match — typically an OSM-unmapped residential
subdivision — the query string is passed as a last resort to the device's native platform geocoder
(on Android, Google's `Geocoder`). Only the query text is sent: no coordinates, contacts, or
identifiers accompany it. Emergency SOS messages are sent directly through Android `SmsManager` over the
cellular network and do not pass through any Alarma server. GPS coordinates, emergency contacts, trip
history, and behavioral data are stored only on the device in an encrypted SQLite database, in
compliance with Republic Act No. 10173 (Data Privacy Act of 2012). The in-app **Settings → Legal**
section provides the full Terms & Conditions and Privacy Policy.

## Appendix C — Reliability & UX Audit Resolution

A focused functional and UX audit ahead of the panel defense closed defects identified during
pre-defense testing while leaving the security posture in [Section 3](#3-security--data-protection)
untouched. Highlights: mandatory master-GPS enforcement before a trip starts; two-way permission
toggles that route to App Settings; the Haversine distance refactor with a jitter gate; full state
reset on stop (no "ghost" active-trip state); monotonic, outlier-resistant stage escalation; notification
deep-linking into the active-trip screen via an explicit `PendingIntent`; and non-intrusive "grey pill"
toast feedback in place of blocking modals.

> Full version-by-version release notes live in [CHANGELOG.md](CHANGELOG.md).

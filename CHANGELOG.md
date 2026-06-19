# Changelog

All notable changes to Alarma are recorded here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [1.2.0] - 2026-06-19

Final defense hardening: the SOS, 911, Bluetooth, and alarm-audio paths now use true native Android
behaviours end to end, with no reliance on the user finishing the job in another app.

### Added

- **True background auto-SMS.** The SOS now sends instantly in the background through
  `Android.Telephony.SmsManager` (multipart-aware) — the rider no longer has to open the messaging app
  and tap *Send*. The restricted `SEND_SMS` permission is requested natively, in context, right before
  the first send, and the "sent" `PendingIntent` is created `FLAG_IMMUTABLE` for Android 12+ compliance.
- **Bluetooth ACL hardware sync.** A single `BroadcastReceiver` now also listens for
  `ACTION_ACL_CONNECTED` / `ACTION_ACL_DISCONNECTED` alongside the adapter state, so plugging earbuds in
  or pulling them out flashes an *Earphones Connected* / *Earphones Disconnected* grey-pill and keeps the
  Settings toggle mirrored to the real hardware.
- **Bundled loud alarm assets.** *Buzzer* and *Siren* are now real audio files shipped in `res/raw`,
  pinned to the alarm channel so Stage 3 plays them at maximum alarm volume on every device.

### Changed

- **Offline-safe, URL-free SOS message.** The SOS body no longer carries a Google Maps link (carriers
  routinely block link-laden SMS). Online it reverse-geocodes the rider's *live* location into a real
  street address — *"EMERGENCY! I need help near {address}."* — and falls back, when offline, to plain
  coordinates a contact can paste into any map app.
- **Replaced *Chime* with *Buzzer*.** The gentle Chime tone was retired from the picker in favour of a
  loud, aggressive buzzer better suited to waking a sleeping commuter.

### Fixed

- **Bulletproof 911 dialer.** A `<queries>` `DIAL`/`tel` declaration in the manifest stops Android 11+
  package-visibility from hiding the dialer, and *Call 911* now uses `Launcher.OpenAsync("tel:911")`,
  surfacing a transient *"Dialer not found on this device"* grey-pill if no dialer can be resolved.
- **Backup restore no longer downgrades the alarm sound.** The backup whitelist was out of sync with the
  real options (it still listed *Chime* and omitted *Bell*/*Siren*/*Buzzer*), which silently reset a
  saved choice to *Default* on restore. It now mirrors the picker exactly.

## [1.1.0] - 2026-06-19

This release is our final capstone-defense polish: it puts data ownership in the commuter's hands,
sharpens the emergency features, and replaces blocking modals with quieter, modern feedback.

### Added

- **User-accessible backup export ("Save As").** Exporting a backup now opens the native OS file
  browser via `FileSaver`, so commuters choose exactly where the encrypted `.alarma` file lands —
  Downloads, Drive, anywhere they own — instead of it being buried in private app storage they can't
  reach. The grey-pill confirmation reports the saved path only after the file is genuinely written.
- **User-driven backup import (File Picker).** Restoring now uses `FilePicker` to let the commuter
  navigate to and select their own `.alarma` file from anywhere on the device. Cancelling the picker
  aborts safely with no crash; the same encrypted decrypt-then-validate-before-clear restore guarantees
  still apply to the picked file.
- **Live Google Maps location in the SOS SMS.** The emergency message now carries a tappable
  `https://maps.google.com/?q={lat},{lon}` link so a contact can jump straight to the rider's position,
  with a clear text-only fallback when no fix is available.
- **Bluetooth hardware-state sync.** The app's Bluetooth UI now mirrors the device's real Bluetooth
  radio in real time — toggling the hardware off flips the in-app switch off and hides the earphone
  status pill, and turning it back on re-syncs the switch automatically.

### Changed

- **Non-intrusive "grey pill" feedback replaces blocking modals.** Backup/restore outcomes and similar
  confirmations now surface as a transient, auto-hiding grey pill (toast-style) near the bottom of the
  screen instead of an intrusive `DisplayAlert` modal — feedback the rider can read at a glance without
  being interrupted mid-commute.
- **Humanized the codebase comments** across the tracking service, backup service, and the active-trip
  view so they read like practical team notes — first-person, with the real reasons and workarounds —
  rather than auto-generated boilerplate.

### Fixed

- **Strict 911 dialer validation.** The *Call 911* action runs `PhoneDialer.Default.Open("911")` inside
  a guarded try/catch and now surfaces a clear alert if the device has no dialer app, instead of failing
  silently.
- **Destination pin no longer disappears on the active-trip map.** A dedicated guard re-asserts the
  destination pin after the Leaflet WebView reloads (and on every location-update cycle), fixing the map
  refresh that intermittently wiped the pin.
- **Map fully resets when a trip is stopped** — the pin, route, and live dot are cleared and the camera
  returns to the default view, instead of staying frozen on the old destination.
- **Individual trip-history deletions now confirm first**, matching the *Clear All* prompt, so a single
  stray tap on a trash icon can't erase a trip without confirmation.

## [Unreleased]

### Added

- **Back navigation arrow on the Trip in Progress view.** A floating top-left arrow lets the rider
  minimise the active-trip screen and return to the tabs without stopping background tracking. It is
  hidden during an alarm stage, so it can't be used to escape the wake-up lockout.
- **Undo snackbar for Favorites.** Removing a favorite now asks for confirmation and then shows a brief
  snackbar with an *Undo* button to instantly restore it.
- **Five distinct alarm sounds.** *Default, Alarm, Chime, Bell, Siren* — each mapped to a different,
  loud system sound so the live preview is clearly differentiable.
- **Adaptive 3-stage progressive alarm escalation.** Gentle Stage 1 at the trigger radius → louder
  Stage 2 at ~50% of the radius → full-screen Emergency lockout at the drop-off, with continuous
  maximum vibration/volume that only the *Slide to Stop* gesture clears. All escalation runs on a
  background thread so the UI never freezes.
- **Overshoot detection with Google Maps hand-off.** Detection from consecutive increasing-distance
  fixes past the stop, handed off to Google Maps via a pure local `google.navigation:q={lat},{lon}`
  Android Intent (zero network).
- **Overshoot recovery flow.** Confirmed full-screen alert (destination name + exact distance overshot)
  → offline *Area Safety Alert* overlay → in-app rerouting screen with a mini-map and offline
  step-by-step return guidance.
- **Live-preview alarm sounds in Settings** that play on selection and stop when leaving the page.
- **Pill-shaped Trip History search** with tappable filter chips (*All*, *Recent*, *By Route*).
- **Trip History deletion** — swipe-to-delete, a per-row trash icon, and a filter-aware *Clear All*.
- **Active-trip details** — the tracking sheet now shows the live current location and the destination.
- **Smooth live map pin** that interpolates between GPS fixes instead of snapping.
- **Validation modals** for the emergency-contact form, replacing quiet inline text.

### Changed

- **Styled dropdown Pickers with chevron icons** replace the alarm-sound chips. Both the Alarm Sound and
  Vibration Intensity pickers are now rounded, button-like pills with a downward chevron and no native
  underline.
- **Humanized the codebase comments** across the tracking, SOS, and controller code so they read like
  practical notes rather than auto-generated boilerplate.
- **Native search bar replaced** with a rounded, seamless lavender pill across the search affordances.
- **Full-screen lockout reserved for the Emergency stage only** — Stages 1 and 2 alert without taking
  over the screen.
- **Filter-aware history clearing** — *Clear All* removes only the trips currently shown when a search or
  chip filter is active.
- **Distance readouts** on the Trip Progress page always show the kilometre unit (e.g. "0.3 km away").

### Fixed

- **GPS accuracy filtering gate (50 m).** Fixes with an accuracy radius wider than 50 m are dropped, so
  cell-tower spikes can't make the pin and distance jump erratically.
- **SOS GPS reliability.** The location fetch now demands highest accuracy with a strict 5-second timeout
  and immediately falls back to the last-known location if it hangs.
- **Strict sequential alarm locking (Stage 1 → 2 → 3).** Stage 2 can only fire after Stage 1, the lead
  distance is capped, and arrival must persist across fixes, so the alarm no longer fires at the wrong
  time or out of order.
- **SOS multi-contact dispatch.** Each contact is messaged in its own try/catch, so one failed number no
  longer aborts delivery to the rest.
- **SOS mandatory location check** that halts dispatch and prompts the user when device location is off.
- **SOS single-fire bug** — the dispatch busy flag is always reset in a `finally`, so the button works on
  every press.
- **Slide-to-Stop gesture** now triggers the moment the thumb crosses the threshold mid-drag, fixing
  Android pan gestures that never raised a completion event.
- **Alarm audio moved off the UI thread**, which also resolved the laggy, unresponsive slider.

### Removed

- **Dead `SelectAndPreviewSoundCommand`** code from the controller (superseded by the dropdown picker).
- **The unsafe plain "Notification" alarm-sound option**, which was too quiet to reliably wake a rider.

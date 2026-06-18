# Changelog

All notable changes to Alarma are recorded here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

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

#!/usr/bin/env bash
# =============================================================================
#  simulate_commute.sh — Alarma Capstone Defense Presentation Script
#  Target device  : Pixel 6 (2) API 35  (default serial: emulator-5554)
#  Destination    : PUP Sta. Mesa, Manila  14.5993° N, 120.9920° E
#  Route direction: North → South  (Valenzuela → España → Legarda → PUP → Pasig)
#  Run time       : ~2 minutes total
# =============================================================================
#
#  PRE-FLIGHT CHECKLIST  (complete before running the script)
#  ──────────────────────────────────────────────────────────
#  1. Launch the Pixel 6 (2) API 35 AVD in Android Studio / emulator.
#  2. Open Alarma on the emulator.
#  3. In Settings → set VehicleType = "UV Express", AlarmLeadMinutes = 8.
#     (These settings make the adaptiveLeadThreshold ≈ 6,700 m, which lets the
#      6.5 km coordinate push cleanly trigger Stage 1.)
#  4. Search for "Polytechnic University of the Philippines, Sta. Mesa, Manila"
#     and select it as the destination.
#  5. Tap "Start Trip" to begin tracking.
#  6. In a terminal window (alongside the emulator): bash simulate_commute.sh
#
#  OPTIONAL: pass the emulator serial as the first argument if your AVD
#  is not on the default port:
#      bash simulate_commute.sh emulator-5556
#
#  WHAT THE PANEL WILL SEE AT EACH STAGE
#  ───────────────────────────────────────
#  STEP 1 — Initialization   : Tracking active, distance chip shows ~18 km.
#  STEP 2 — 6.5 km approach  : Stage 1 "Approaching Stop" banner fires;
#                               audio alarm activates at approach volume.
#  STEP 3 — 2.1 km proximity : Distance chip refreshes; if Stage 1 fired early
#                               the alarm escalates, otherwise Stage 1 fires here.
#  STEP 4 — Destination core : Distance ≤ 200 m → Stage 2 "Get Ready" triggers;
#                               full-screen high-contrast overlay appears with
#                               pulsing "YOU MIGHT MISS YOUR STOP" alert text.
#  STEP 5 — Overshoot        : Distance surpasses 450 m past target →
#                               Stage 3 "Overshoot Detected" modal fires with
#                               the "Open in GMaps" reroute action handler.
# =============================================================================

set -euo pipefail

# ── Configuration ──────────────────────────────────────────────────────────
ADB_SERIAL="${1:-emulator-5554}"

# Destination: PUP Sta. Mesa main campus gate
DEST_LAT="14.59930"
DEST_LON="120.99200"

# Colours for terminal output
RED='\033[0;31m'; GRN='\033[0;32m'; YLW='\033[1;33m'; CYN='\033[0;36m'; NC='\033[0m'

# ── Helper: inject a GPS coordinate and wait ────────────────────────────────
# NOTE: adb emu geo fix takes <longitude> <latitude> (lon first)
push_location() {
    local label="$1" lat="$2" lon="$3" wait_sec="$4"
    printf "\n${CYN}▶  %s${NC}\n" "${label}"
    printf "   Injecting → lat=%-10s  lon=%s\n" "${lat}" "${lon}"
    adb -s "${ADB_SERIAL}" emu geo fix "${lon}" "${lat}"
    printf "   ${YLW}Holding %ss — observe the UI transition before continuing...${NC}\n" "${wait_sec}"
    sleep "${wait_sec}"
}

# ── Banner ──────────────────────────────────────────────────────────────────
printf "\n${GRN}===================================================================${NC}\n"
printf "${GRN}  ALARMA — Capstone Defense Commute Simulation${NC}\n"
printf "${GRN}  Destination : PUP Sta. Mesa  (%s, %s)${NC}\n" "${DEST_LAT}" "${DEST_LON}"
printf "${GRN}  ADB target  : %s${NC}\n" "${ADB_SERIAL}"
printf "${GRN}===================================================================${NC}\n\n"

# ── Pre-flight ADB connectivity check ──────────────────────────────────────
printf "Verifying ADB connection to %s...\n" "${ADB_SERIAL}"
if ! adb -s "${ADB_SERIAL}" shell echo "ping" > /dev/null 2>&1; then
    printf "${RED}ERROR: Cannot reach %s. Ensure the emulator is running and ADB is in PATH.${NC}\n" "${ADB_SERIAL}"
    printf "  Run: adb devices  — to list available serials.\n"
    exit 1
fi
printf "${GRN}ADB connection confirmed.${NC}\n\n"
printf "Starting simulation in 5 seconds...  (Ctrl-C to abort)\n"
sleep 5

# ══════════════════════════════════════════════════════════════════════════════
#  STEP 1 — Initialization Node
#  Coordinates : ~18 km north of PUP  (Valenzuela / North Caloocan corridor)
#  App state   : Tracking active, no alarm stage, distance chip shows ~18 km.
#  Presenter   : "We begin the simulated commute in Valenzuela, well outside
#                 all geofence zones. The app is tracking in the background."
# ══════════════════════════════════════════════════════════════════════════════
push_location \
    "STEP 1 │ INITIALIZATION  — ~18 km out  (Valenzuela corridor)" \
    "14.76130" "120.99200" 20

# ══════════════════════════════════════════════════════════════════════════════
#  STEP 2 — Stage 1 Arrival Node  (6.5 km from PUP)
#  Coordinates : España / Sampaloc intersection corridor
#  Threshold   : With UV Express @ ~50 km/h and AlarmLeadMinutes = 8,
#                adaptiveLeadDistance = 13.9 m/s × 8 min × 60 s ≈ 6,670 m.
#                distanceToDestination (6,500 m) ≤ adaptiveLeadThreshold → Stage 1 fires.
#  App state   : "Approaching Stop" card inflates; Stage 1 audio alarm activates.
#  Presenter   : "At 6.5 km out the approach card overlay animates into view and
#                 Stage 1 audio begins — the commuter is warned to prepare."
# ══════════════════════════════════════════════════════════════════════════════
push_location \
    "STEP 2 │ STAGE 1 APPROACH  — 6.5 km out  (España corridor)" \
    "14.65780" "120.99200" 25

# ══════════════════════════════════════════════════════════════════════════════
#  STEP 3 — Stage 2 Proximity Node  (2.1 km from PUP)
#  Coordinates : R. Hidalgo / Legarda corridor
#  App state   : Distance chip refreshes to "2 km away". If Stage 1 audio is
#                still active the controller re-evaluates volume priority.
#                Silent / DND-override path remains armed for Stage 2 trigger.
#  Presenter   : "2.1 km from the destination — mid-approach. The chip distance
#                 text updates in real time and the alarm audio volume ramps up."
# ══════════════════════════════════════════════════════════════════════════════
push_location \
    "STEP 3 │ STAGE 2 PROXIMITY  — 2.1 km out  (Legarda corridor)" \
    "14.61820" "120.99200" 25

# ══════════════════════════════════════════════════════════════════════════════
#  STEP 4 — Stage 3 Breach Node  (inside 200 m arrival radius)
#  Coordinates : PUP main campus gate (~50 m from target centroid)
#  Threshold   : ArrivalThresholdMeters = 200 m → _hasArrivedAtDestination = true
#                → AlarmStage.Stage2 fires ("Get Ready / You are near your destination").
#  App state   : High-contrast full-screen red overlay; "WAKE UP" pulsing text;
#                Stage 2 audio with DND / silent-mode override active.
#  Presenter   : "We are inside the arrival radius. Stage 2 fires — the full-screen
#                 warning wrapper activates with critical audio override."
# ══════════════════════════════════════════════════════════════════════════════
push_location \
    "STEP 4 │ DESTINATION BREACH  — <200 m  (PUP main gate)" \
    "14.59980" "120.99200" 25

# ══════════════════════════════════════════════════════════════════════════════
#  STEP 5 — Overshoot Multi-Block  (1.2 km past destination boundary)
#  Coordinates : ~1.2 km south of PUP toward Pasig corridor
#  Threshold   : OvershootThresholdMeters = ArrivalThresholdMeters (200) +
#                OvershootBufferMeters (250) = 450 m.
#                _hasArrivedAtDestination is true; distance (1,200 m) ≥ 450 m
#                → _overshootAlerted = true; IsOvershootPending = true;
#                → AlarmStage.Stage3 fires.
#  App state   : "Overshoot Detected" modal with OvershootDistanceText and
#                "Open in GMaps" external reroute action button.
#  Presenter   : "The commuter has passed the stop. Stage 3 overshoot detection
#                 fires — the warning modal appears with a one-tap GMaps reroute."
# ══════════════════════════════════════════════════════════════════════════════
push_location \
    "STEP 5 │ OVERSHOOT DETECTED  — +1.2 km south  (Pasig corridor)" \
    "14.58850" "120.99200" 15

# ── Summary ─────────────────────────────────────────────────────────────────
printf "\n${GRN}===================================================================${NC}\n"
printf "${GRN}  Simulation complete. All 5 nodes demonstrated successfully.${NC}\n"
printf "${GRN}  Tap \"Stop Trip\" in Alarma to end the tracking session.${NC}\n"
printf "${GRN}===================================================================${NC}\n\n"

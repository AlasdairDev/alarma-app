#!/usr/bin/env python3
"""
simulate_commute.py  —  Alarma zero-touch commute simulator
===========================================================

Fully hands-free: launches the app, taps into Search, types a destination,
submits, starts tracking, then injects an interpolated ~1 km northbound
commute (28 points, 4s apart) so the alarm UI advances through
Approach -> Arrival -> Overshoot.

╔══════════════════════════════════════════════════════════════════════╗
║  TAP COORDINATES — EDIT THESE FOR YOUR EMULATOR                       ║
║  Values below were read live from a 1080x2400 emulator via            ║
║  `uiautomator dump`. If your window scale differs, recalibrate:       ║
║    • Settings > System > Developer options > "Pointer location" = ON   ║
║    • or:  adb shell uiautomator dump /sdcard/ui.xml && adb pull ...    ║
║          bounds="[x1,y1][x2,y2]"  ->  center ((x1+x2)//2,(y1+y2)//2)   ║
║  Confirm screen size first:  adb shell wm size                        ║
╚══════════════════════════════════════════════════════════════════════╝
"""

import argparse
import os
import subprocess
import sys
import time

PACKAGE = "com.alarma.app"

# ── EDITABLE TAP COORDINATES (X, Y in screen pixels) ────────────────────────
# The Alarma search is a TWO-screen flow: the Home "Search" bar opens a
# separate Search page, where the actual text field lives.
TAP_SEARCH_OPEN_X,  TAP_SEARCH_OPEN_Y  = 540, 523    # Home "Search" bar  [108,495][973,548]
TAP_SEARCH_FIELD_X, TAP_SEARCH_FIELD_Y = 633, 649    # Search page entry  [268,597][999,702]
TAP_FIRST_RESULT_X, TAP_FIRST_RESULT_Y = 540, 850    # first result row (verified: tapped 540,850 selects row 1)
TAP_START_TRACKING_X, TAP_START_TRACKING_Y = 854, 2051  # VERIFIED Start Trip center (bounds [725,1999][983,2103])

# ── EDITABLE TIMINGS ────────────────────────────────────────────────────────
WAIT_UI_LOAD   = 10    # after launch (covers the launch animation + Home init)
WAIT_NAV       = 2     # after opening search, for page navigation
WAIT_RESOLVE   = 3     # after Enter, for geocoder/results
WAIT_AFTER_SELECT = 2  # after picking a result / starting tracking
INTERVAL_SECONDS  = 4  # delay per GPS fix

# ── DESTINATION typed into the search box ───────────────────────────────────
# NOTE: in-app search resolves PLACE NAMES via geocoding; a raw "lat,lon"
# string may not drop a pin. Use a landmark near the route end for a reliable
# arrival/overshoot, e.g. "Espana" or "University of Santo Tomas".
DESTINATION_TEXT = "University of Santo Tomas"

# ── Route: straight line that APPROACHES, ARRIVES AT, then OVERSHOOTS the
#    destination so all three alarm stages fire. The line runs S->N along the
#    UST longitude, passing through UST (~14.6097,120.9895) near the midpoint
#    (Arrival), then continuing ~0.7 km past it (Overshoot).
ROUTE_START = (14.6047, 120.9895)   # ~0.55 km south of UST (approach)
ROUTE_END   = (14.6167, 120.9895)   # ~0.78 km north of UST (overshoot)
POINTS      = 28


def parse_args():
    p = argparse.ArgumentParser(description="Zero-touch Alarma commute simulation via ADB.")
    p.add_argument("-s", "--serial", default=None, help="Target serial (adb -s). Auto-detected if omitted.")
    p.add_argument("--interval", type=float, default=INTERVAL_SECONDS, help="Seconds between GPS fixes (default 4).")
    p.add_argument("--points", type=int, default=POINTS, help="Interpolated waypoint count (default 28).")
    p.add_argument("--skip-ui", action="store_true", help="Inject GPS only; skip launch/tap/type.")
    return p.parse_args()


def build_route(points):
    points = max(points, 2)
    lat0, lon0 = ROUTE_START
    lat1, lon1 = ROUTE_END
    n = points - 1
    return [(lat0 + (lat1 - lat0) * i / n, lon0 + (lon1 - lon0) * i / n) for i in range(points)]


def total_distance_km():
    return abs(ROUTE_END[0] - ROUTE_START[0]) * 111.0


def detect_device(serial):
    try:
        out = subprocess.run(["adb", "devices"], capture_output=True, text=True, check=True).stdout
    except FileNotFoundError:
        print("[FATAL] 'adb' not found on PATH. Install platform-tools and retry.")
        sys.exit(1)
    devices = [ln.split("\t")[0] for ln in out.splitlines()[1:] if "\tdevice" in ln]
    if not devices:
        print("[FATAL] No authorized devices/emulators attached. Run 'adb devices'.")
        sys.exit(1)
    if serial and serial not in devices:
        print(f"[FATAL] Serial '{serial}' not attached: {devices}")
        sys.exit(1)
    if not serial and len(devices) > 1:
        print(f"[FATAL] Multiple targets {devices}; pass one with -s SERIAL.")
        sys.exit(1)
    return serial or devices[0]


def adb_prefix(serial):
    return "adb" + (f" -s {serial}" if serial else "")


def run(cmd):
    print(f"    $ {cmd}")
    return os.system(cmd)


def adb_type(adb, text):
    # `adb shell input text` splits on spaces; %s is the documented space escape.
    return run(f'{adb} shell input text "{text.replace(" ", "%s")}"')


def automate_ui(adb):
    print("-" * 62)
    print(" ZERO-TOUCH UI AUTOMATION")
    print("-" * 62)

    print(" [1] Launch app")
    run(f'{adb} shell monkey -p {PACKAGE} -c android.intent.category.LAUNCHER 1')

    print(f" [2] Wait {WAIT_UI_LOAD}s for UI load")
    time.sleep(WAIT_UI_LOAD)

    print(f" [3] Tap Home Search bar ({TAP_SEARCH_OPEN_X},{TAP_SEARCH_OPEN_Y})")
    run(f'{adb} shell input tap {TAP_SEARCH_OPEN_X} {TAP_SEARCH_OPEN_Y}')
    time.sleep(WAIT_NAV)

    print(f" [3b] Tap Search field ({TAP_SEARCH_FIELD_X},{TAP_SEARCH_FIELD_Y})")
    run(f'{adb} shell input tap {TAP_SEARCH_FIELD_X} {TAP_SEARCH_FIELD_Y}')
    time.sleep(1)

    print(f" [4] Type destination: {DESTINATION_TEXT}")
    adb_type(adb, DESTINATION_TEXT)

    print(" [5] Press Enter (keyevent 66)")
    run(f'{adb} shell input keyevent 66')

    print(f" [6] Wait {WAIT_RESOLVE}s for geocoder/results")
    time.sleep(WAIT_RESOLVE)

    print(f" [6b] Tap first result ({TAP_FIRST_RESULT_X},{TAP_FIRST_RESULT_Y}) to set destination")
    run(f'{adb} shell input tap {TAP_FIRST_RESULT_X} {TAP_FIRST_RESULT_Y}')
    time.sleep(WAIT_AFTER_SELECT)

    # Settle: ensure the UI thread is fully ready so the tap isn't dropped
    # ("input tap killed by system") right after the result-select redraw.
    print(" [7] Settling 2s before Start Tracking tap")
    time.sleep(2)
    print(f" [7b] Tap Start Tracking ({TAP_START_TRACKING_X},{TAP_START_TRACKING_Y})")
    # `input motionevent` down/up is more robust than `input tap`, which the
    # system occasionally drops during a transition. Fall back to tap if needed.
    rc = run(f'{adb} shell input tap {TAP_START_TRACKING_X} {TAP_START_TRACKING_Y}')
    time.sleep(1)
    if rc != 0:
        print("     tap returned non-zero; retrying once")
        run(f'{adb} shell input tap {TAP_START_TRACKING_X} {TAP_START_TRACKING_Y}')
    time.sleep(WAIT_AFTER_SELECT)


def drive(adb, route, span_km, interval):
    print("-" * 62)
    print(" [8] DRIVING — 28-point northbound GPS loop")
    print("-" * 62)
    total = len(route)
    for i, (lat, lon) in enumerate(route, start=1):
        progress = span_km * (i - 1) / (total - 1)
        rc = os.system(f'{adb} emu geo fix {lon:.6f} {lat:.6f}')
        print(f"[{i:2d}/{total}] fix lat={lat:.6f} lon={lon:.6f}  (~{progress:.2f} km)  -> "
              f"{'OK' if rc == 0 else f'FAILED rc={rc}'}")
        if i < total:
            time.sleep(interval)


def main():
    args = parse_args()
    serial = detect_device(args.serial)
    adb = adb_prefix(serial)
    route = build_route(args.points)
    span_km = total_distance_km()

    print("=" * 62)
    print(" ALARMA COMMUTE SIMULATION (zero-touch)")
    print("=" * 62)
    print(f" Target    : {serial}")
    print(f" Waypoints : {len(route)} @ {args.interval}s   Distance: ~{span_km:.2f} km N")
    print("=" * 62)

    if args.skip_ui:
        print(" (--skip-ui) GPS injection only.")
    else:
        automate_ui(adb)

    drive(adb, route, span_km, args.interval)

    print("-" * 62)
    print(" Complete. Verify Stage 1 (Approach) -> 2 (Arrival) -> 3 (Overshoot).")
    print("=" * 62)


if __name__ == "__main__":
    main()

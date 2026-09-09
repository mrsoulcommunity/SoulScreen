#!/usr/bin/env python3
"""Verify SoulScreen's binary property-list writer against an independent implementation.

Run the test suite first - PlistInteropFixture writes the file this reads:

    dotnet test tests/SoulScreen.Tests
    python tools/check-plist-interop.py

Python's plistlib implements Apple's CFBinaryPlist layout, so agreeing with it is good
evidence iOS will parse what SoulScreen sends in /info and the SETUP responses.
"""
import os
import plistlib
import sys
import tempfile

PATH = os.path.join(tempfile.gettempdir(), "soulscreen-interop.plist")

EXPECTED = {
    "deviceID": "AA:BB:CC:DD:EE:FF",
    "features": 1224129983,
    "pk": b"\xde\xad\xbe\xef",
    "name": "SoulScreen کسری",
    "statusFlags": 68,
    "initialVolume": -30.5,
    "keepAliveSendStatsAsBody": True,
    "negative": -1,
    "many": list(range(32)),
}


def main() -> int:
    # The Windows console defaults to cp1252, which cannot print the non-Latin test value.
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    if not os.path.exists(PATH):
        print(f"missing {PATH} - run 'dotnet test tests/SoulScreen.Tests' first", file=sys.stderr)
        return 2

    with open(PATH, "rb") as handle:
        document = plistlib.load(handle)

    failures = []
    for key, expected in EXPECTED.items():
        actual = document.get(key)
        if isinstance(expected, float):
            ok = actual is not None and abs(actual - expected) < 1e-9
        else:
            ok = actual == expected
        status = "ok  " if ok else "FAIL"
        print(f"  {status} {key} = {actual!r}")
        if not ok:
            failures.append(f"{key}: expected {expected!r}, got {actual!r}")

    display = (document.get("displays") or [{}])[0]
    for key, expected in (("width", 1920), ("height", 1080), ("overscanned", False)):
        ok = display.get(key) == expected
        print(f"  {'ok  ' if ok else 'FAIL'} displays[0].{key} = {display.get(key)!r}")
        if not ok:
            failures.append(f"displays[0].{key}")

    refresh = display.get("refreshRate")
    ok = refresh is not None and abs(refresh - 1 / 60) < 1e-12
    print(f"  {'ok  ' if ok else 'FAIL'} displays[0].refreshRate = {refresh!r}")
    if not ok:
        failures.append("displays[0].refreshRate")

    print()
    if failures:
        print(f"{len(failures)} check(s) failed:")
        for failure in failures:
            print(f"  - {failure}")
        return 1

    print("SoulScreen's binary plist writer agrees with plistlib.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

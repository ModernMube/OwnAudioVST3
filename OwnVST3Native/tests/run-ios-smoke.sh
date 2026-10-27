#!/bin/bash
# usage: run-ios-smoke.sh <ownvst3.xcframework>
# Builds IosSmokeTest.cpp against the simulator slice and runs it in a booted (or freshly booted) simulator.
set -euo pipefail

XCF="$1"
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="$(mktemp -d)"
SLICE="$(ls -d "$XCF"/ios-*simulator)"

xcrun --sdk iphonesimulator clang++ -std=c++17 -target arm64-apple-ios15.0-simulator \
    -I"$HERE/../include" "$HERE/IosSmokeTest.cpp" \
    -F"$SLICE" -framework ownvst3 -Wl,-rpath,"$SLICE" -o "$OUT/smoke"

DEV="$(xcrun simctl list devices booted -j | python3 -c 'import json,sys; d=[x for v in json.load(sys.stdin)["devices"].values() for x in v]; print(d[0]["udid"] if d else "")')"
if [ -z "$DEV" ]; then
  DEV="$(xcrun simctl list devices available -j | python3 -c 'import json,sys; d=[x for k,v in json.load(sys.stdin)["devices"].items() if "iOS" in k for x in v if "iPhone" in x["name"] or "iPad" in x["name"]]; print(d[-1]["udid"])')"
  xcrun simctl boot "$DEV"
  xcrun simctl bootstatus "$DEV" -b > /dev/null
fi

xcrun simctl spawn "$DEV" "$OUT/smoke"

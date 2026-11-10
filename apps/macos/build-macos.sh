#!/bin/sh
set -eu
cd "$(dirname "$0")"
swift build -c release
APP=".build/Drift.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp .build/release/DriftMac "$APP/Contents/MacOS/DriftMac"
cp Info.plist "$APP/Contents/Info.plist"
codesign --force --deep --sign - "$APP"
printf 'Built %s\n' "$APP"

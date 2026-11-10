#!/bin/sh
set -eu
cd "$(dirname "$0")"
swift build -c release
BIN_PATH="$(swift build --show-bin-path -c release)"
APP=".build/Drift.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN_PATH/DriftMac" "$APP/Contents/MacOS/DriftMac"
cp Info.plist "$APP/Contents/Info.plist"
codesign --force --deep --sign - "$APP"
printf 'Built %s\n' "$APP"

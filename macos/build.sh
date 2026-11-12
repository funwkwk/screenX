#!/bin/bash
# Builds build/screenX.app (universal: Apple silicon + Intel) and build/screenX-<version>-macOS.dmg.
# Usage on a Mac with Xcode command-line tools:  bash macos/build.sh [version]
set -euo pipefail
cd "$(dirname "$0")"
VERSION="${1:-1.1.0}"

swift build -c release --arch arm64 --arch x86_64
BIN=".build/apple/Products/Release/screenX"

APP="build/screenX.app"
rm -rf build
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/screenX"
sed "s/__VERSION__/$VERSION/g" Info.plist > "$APP/Contents/Info.plist"

# app icon from the shared PNG
ICONSET="build/AppIcon.iconset"
mkdir -p "$ICONSET"
for s in 16 32 128 256 512; do
  d=$((s * 2))
  sips -z "$s" "$s" ../Assets/screenX.png --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
  sips -z "$d" "$d" ../Assets/screenX.png --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"
rm -rf "$ICONSET"

# ad-hoc signature (no Apple Developer account): Gatekeeper asks once on first open
codesign --force --deep --sign - "$APP"

mkdir -p build/dmg
cp -R "$APP" build/dmg/
ln -s /Applications build/dmg/Applications
hdiutil create -volname "screenX" -srcfolder build/dmg -ov -format UDZO "build/screenX-$VERSION-macOS.dmg"
rm -rf build/dmg
ls -lh build

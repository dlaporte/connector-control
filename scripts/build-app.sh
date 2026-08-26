#!/bin/bash
# Assembles build/Connector Control.app from the SwiftPM build products.
#
# Environment knobs (all optional; defaults produce a local dev build):
#   VERSION           marketing version for Info.plist        (default: 1.0)
#   BUILD_NUMBER      CFBundleVersion                         (default: 1)
#   SIGNING_IDENTITY  codesign identity                       (default: "-", ad-hoc)
#                     A real Developer ID identity also enables the hardened
#                     runtime + secure timestamp that notarization requires.
#   UNIVERSAL=1       build arm64 + x86_64 (CI/release builds)
set -euo pipefail
cd "$(dirname "$0")/.."

APP="build/Connector Control.app"
VERSION="${VERSION:-1.0}"
BUILD_NUMBER="${BUILD_NUMBER:-1}"
SIGNING_IDENTITY="${SIGNING_IDENTITY:--}"

if [ "${UNIVERSAL:-0}" = "1" ]; then
    swift build -c release --arch arm64 --arch x86_64
    BIN=".build/apple/Products/Release/ConnectorControl"
else
    swift build -c release
    BIN=".build/release/ConnectorControl"
fi

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS"
cp "$BIN" "$APP/Contents/MacOS/Connector Control"

# Embed Sparkle: SwiftPM links it as @rpath but doesn't assemble bundles, so
# the framework is copied out of the resolved artifact and the executable is
# pointed at Contents/Frameworks. (cp -R preserves the framework's symlink
# structure; ditto would flatten it.)
SPARKLE_FRAMEWORK=".build/artifacts/sparkle/Sparkle/Sparkle.xcframework/macos-arm64_x86_64/Sparkle.framework"
mkdir -p "$APP/Contents/Frameworks"
cp -R "$SPARKLE_FRAMEWORK" "$APP/Contents/Frameworks/"
install_name_tool -add_rpath "@executable_path/../Frameworks" \
    "$APP/Contents/MacOS/Connector Control" 2>/dev/null || true

mkdir -p "$APP/Contents/Resources"
swift scripts/generate-icon.swift "$APP/Contents/Resources/AppIcon.icns"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
  "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key><string>Connector Control</string>
    <key>CFBundleIconFile</key><string>AppIcon</string>
    <key>CFBundleIdentifier</key><string>com.dlaporte.connector-control</string>
    <key>CFBundleName</key><string>Connector Control</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>${VERSION}</string>
    <key>CFBundleVersion</key><string>${BUILD_NUMBER}</string>
    <key>LSMinimumSystemVersion</key><string>14.0</string>
    <key>LSUIElement</key><true/>
    <key>NSHumanReadableCopyright</key><string>© 2026 David LaPorte</string>
    <key>SUFeedURL</key><string>https://github.com/dlaporte/connector-control/releases/latest/download/appcast.xml</string>
    <key>SUPublicEDKey</key><string>UmpM6nLMC8udcgUZ4IYigUgqFHziHPNsYilHc7Nn/3Q=</string>
    <key>SUEnableAutomaticChecks</key><true/>
    <key>SUAutomaticallyUpdate</key><true/>
</dict>
</plist>
PLIST

if [ "$SIGNING_IDENTITY" = "-" ]; then
    # Local dev: Sparkle keeps its own shipped signature; ad-hoc sign the app.
    codesign --force --sign - "$APP"
else
    # Notarization requires every nested Mach-O be signed with the same
    # Developer ID + hardened runtime, inside-out per Sparkle's non-Xcode
    # signing docs: XPC services, Autoupdate, Updater.app, the framework,
    # then the app. (No --deep: it mis-signs nested bundles.)
    FRAMEWORK="$APP/Contents/Frameworks/Sparkle.framework"
    codesign --force --options runtime --timestamp --preserve-metadata=entitlements \
        --sign "$SIGNING_IDENTITY" "$FRAMEWORK/Versions/B/XPCServices/Downloader.xpc"
    codesign --force --options runtime --timestamp --preserve-metadata=entitlements \
        --sign "$SIGNING_IDENTITY" "$FRAMEWORK/Versions/B/XPCServices/Installer.xpc"
    codesign --force --options runtime --timestamp \
        --sign "$SIGNING_IDENTITY" "$FRAMEWORK/Versions/B/Autoupdate"
    codesign --force --options runtime --timestamp \
        --sign "$SIGNING_IDENTITY" "$FRAMEWORK/Versions/B/Updater.app"
    codesign --force --options runtime --timestamp \
        --sign "$SIGNING_IDENTITY" "$FRAMEWORK"
    # Hardened runtime + timestamp are required for notarization.
    codesign --force --options runtime --timestamp \
        --sign "$SIGNING_IDENTITY" "$APP"
fi
codesign --verify --strict "$APP"
echo "Built: $APP (version ${VERSION}, signed: ${SIGNING_IDENTITY})"
echo "Install: cp -R \"$APP\" /Applications/"

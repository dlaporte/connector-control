#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."

APP="build/MCP Enabler.app"
swift build -c release

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS"
cp .build/release/MCPEnabler "$APP/Contents/MacOS/MCP Enabler"

cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
  "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key><string>MCP Enabler</string>
    <key>CFBundleIdentifier</key><string>com.dlaporte.mcp-enabler</string>
    <key>CFBundleName</key><string>MCP Enabler</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>1.0</string>
    <key>CFBundleVersion</key><string>1</string>
    <key>LSMinimumSystemVersion</key><string>14.0</string>
    <key>LSUIElement</key><true/>
    <key>NSHumanReadableCopyright</key><string></string>
</dict>
</plist>
PLIST

codesign --force --sign - "$APP"
echo "Built: $APP"
echo "Install: cp -R \"$APP\" /Applications/"

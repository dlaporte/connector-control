#!/usr/bin/env bash
# Assembles build/Connector Control.app from the SwiftPM build products.
#
# Environment knobs (all optional; defaults produce a local dev build):
#   VERSION           marketing version for Info.plist        (default: 1.0)
#   BUILD_NUMBER      CFBundleVersion                         (default: derived from VERSION)
#   SIGNING_IDENTITY  codesign identity                       (default: "-", ad-hoc)
#                     A real Developer ID identity also enables the hardened
#                     runtime + secure timestamp that notarization requires.
#   UNIVERSAL=1       build arm64 + x86_64 (CI/release builds)
set -euo pipefail
cd "$(dirname "$0")/.."

APP="build/Connector Control.app"
VERSION="${VERSION:-1.0}"
SIGNING_IDENTITY="${SIGNING_IDENTITY:--}"

# CFBundleVersion is Sparkle's update-comparison key, so it must be derived
# from the semantic version, not a CI run counter - with a run counter, any
# later-built release of an OLDER version outranks newer ones in the feed
# (e.g. re-tagging v1.2.2 after v1.3.0 shipped would auto-"update" users
# backward). major*10000 + minor*100 + patch is monotonic across versions
# and, at >=10000, safely above the run numbers releases <=1.2.2 shipped
# with. A preview of X.Y.Z (X.Y.Z-preview.N) gets the number just below the
# release's, with N as a second component: 1.4.0-preview.3 is 10399.3, so
# Sparkle ranks it above every earlier release, below 1.4.0 (which the
# preview feed offers it when that ships), and previews in order among
# themselves. Any other -suffix (1.3.0-beta1) is ignored for the numeric
# derivation.
if [ -z "${BUILD_NUMBER:-}" ]; then
    BASE="${VERSION%%-*}"
    IFS=. read -r MAJ MIN PAT <<< "$BASE"
    BUILD_NUMBER=$(( ${MAJ:-0} * 10000 + ${MIN:-0} * 100 + ${PAT:-0} ))
    case "$VERSION" in
        *-preview.*) BUILD_NUMBER="$(( BUILD_NUMBER - 1 )).${VERSION##*-preview.}" ;;
    esac
fi

# SUFeedURL: which appcast this build checks. A preview (X.Y.Z-preview.N) reads the preview
# feed, the appcast.xml of the permanent preview-feed prerelease, which every published preview
# and every stable release replaces (scripts/release/update-preview-feed.sh) - so a preview
# install moves on to later previews and to the final release, as a Windows preview does.
# Everything else reads the stable feed at releases/latest, which a prerelease never is. Decided
# here, from the version alone, and not from BUILD_NUMBER, which a caller may preset; the plist
# check below fails the build if the pairing ever comes out otherwise.
STABLE_FEED="https://github.com/dlaporte/connector-control/releases/latest/download/appcast.xml"
PREVIEW_FEED="https://github.com/dlaporte/connector-control/releases/download/preview-feed/appcast.xml"
case "$VERSION" in
    *-preview.*) FEED_URL="$PREVIEW_FEED" ;;
    *)           FEED_URL="$STABLE_FEED" ;;
esac

if [ "${UNIVERSAL:-0}" = "1" ]; then
    swift build -c release --product ConnectorControl --arch arm64 --arch x86_64
    swift build -c release --product connector-control --arch arm64 --arch x86_64
    BIN=".build/apple/Products/Release/ConnectorControl"
    TOOL=".build/apple/Products/Release/connector-control"
else
    swift build -c release --product ConnectorControl
    swift build -c release --product connector-control
    BIN=".build/release/ConnectorControl"
    TOOL=".build/release/connector-control"
fi

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS"
cp "$BIN" "$APP/Contents/MacOS/Connector Control"
# The command-line tool Claude's entry runs by its full path, beside the app's own executable. It links
# nothing from Contents/Frameworks, so it needs no rpath.
cp "$TOOL" "$APP/Contents/MacOS/connector-control"

# Embed Sparkle: SwiftPM links it as @rpath but doesn't assemble bundles, so
# the framework is copied out of the resolved artifact and the executable is
# pointed at Contents/Frameworks. (cp -R preserves the framework's symlink
# structure; ditto would flatten it.)
SPARKLE_FRAMEWORK=".build/artifacts/sparkle/Sparkle/Sparkle.xcframework/macos-arm64_x86_64/Sparkle.framework"
mkdir -p "$APP/Contents/Frameworks"
cp -R "$SPARKLE_FRAMEWORK" "$APP/Contents/Frameworks/"
# This rpath is load-bearing: Sparkle is linked as @rpath/Sparkle.framework
# and no linker-emitted rpath resolves it in the installed bundle, so a
# missing entry ships an app that dies at launch with "Library not loaded" -
# and nothing later in the pipeline (codesign, notarization, spctl) exercises
# dyld. Fail loudly here, and verify the load command actually landed. (The
# bundle is assembled fresh each run, so a benign "would duplicate path"
# failure cannot occur.)
install_name_tool -add_rpath "@executable_path/../Frameworks" \
    "$APP/Contents/MacOS/Connector Control"
otool -l "$APP/Contents/MacOS/Connector Control" \
    | grep -q "@executable_path/../Frameworks" \
    || { echo "error: Frameworks rpath missing from executable" >&2; exit 1; }

mkdir -p "$APP/Contents/Resources"
swift scripts/generate-icon.swift "$APP/Contents/Resources/AppIcon.icns"

# SUAutomaticallyUpdate is false: Sparkle still checks on its schedule and
# shows the update, but installing it is a click, not something that happens
# to a background menu-bar agent with no Dock presence while nobody is
# looking. Whoever prefers silent installs turns them on in Settings ▸ General
# (Sparkle persists that choice itself). Signed updates are only as safe as
# the signing key, and a prompt is what turns a compromised key into a dialog
# a person can refuse instead of code that runs everywhere.
# SUVerifyUpdateBeforeExtraction: the DMG's EdDSA signature is checked before anything is
# unpacked, not after. SURequireSignedFeed (Sparkle 2.9+): the appcast and its release notes
# must be signed too; generate_appcast signs them when it sees this key in the app it is
# publishing. Both keys bind the whole update path to SUPublicEDKey, whose private half
# lives only in the release job.
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
    <key>SUFeedURL</key><string>${FEED_URL}</string>
    <key>SUPublicEDKey</key><string>UmpM6nLMC8udcgUZ4IYigUgqFHziHPNsYilHc7Nn/3Q=</string>
    <key>SUEnableAutomaticChecks</key><true/>
    <key>SUAutomaticallyUpdate</key><false/>
    <key>SUVerifyUpdateBeforeExtraction</key><true/>
    <key>SURequireSignedFeed</key><true/>
</dict>
</plist>
PLIST

# The feed pairing, read back from the plist just written: a stable install pointed at the
# preview feed would be offered previews, and a preview pointed at the stable feed would never
# see the next preview. Either is a build failure, on every CI, preview and release build.
WRITTEN_FEED=$(/usr/libexec/PlistBuddy -c 'Print :SUFeedURL' "$APP/Contents/Info.plist")
case "$VERSION" in
    *-preview.*) WANT_FEED="$PREVIEW_FEED" ;;
    *)           WANT_FEED="$STABLE_FEED" ;;
esac
if [ "$WRITTEN_FEED" != "$WANT_FEED" ]; then
    echo "error: version $VERSION was given SUFeedURL $WRITTEN_FEED, expected $WANT_FEED" >&2
    exit 1
fi
# Stated a second time on its own terms, independent of the URL constants above.
if [[ "$VERSION" != *-preview.* && "$WRITTEN_FEED" == *preview-feed* ]]; then
    echo "error: stable version $VERSION must never read the preview feed ($WRITTEN_FEED)" >&2
    exit 1
fi

# Every nested executable is signed before the app, the command-line tool included: codesign does not
# sign other executables in Contents/MacOS along with the app, and strict verification - and
# notarization - refuse one that is unsigned.
TOOL_ID="com.dlaporte.connector-control.cli"
if [ "$SIGNING_IDENTITY" = "-" ]; then
    # Local dev: Sparkle keeps its own shipped signature; ad-hoc sign the tool, then the app.
    codesign --force --sign - --identifier "$TOOL_ID" "$APP/Contents/MacOS/connector-control"
    codesign --force --sign - "$APP"
else
    # Notarization requires every nested Mach-O be signed with the same
    # Developer ID + hardened runtime, inside-out per Sparkle's non-Xcode
    # signing docs: XPC services, Autoupdate, Updater.app, the framework,
    # then the command-line tool, then the app. (No --deep: it mis-signs nested bundles.)
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
    codesign --force --options runtime --timestamp --identifier "$TOOL_ID" \
        --sign "$SIGNING_IDENTITY" "$APP/Contents/MacOS/connector-control"
    # Hardened runtime + timestamp are required for notarization.
    codesign --force --options runtime --timestamp \
        --sign "$SIGNING_IDENTITY" "$APP"
fi
codesign --verify --strict "$APP"
codesign --verify --strict "$APP/Contents/MacOS/connector-control"
echo "Built: $APP (version ${VERSION}, signed: ${SIGNING_IDENTITY})"
echo "Install: cp -R \"$APP\" /Applications/"

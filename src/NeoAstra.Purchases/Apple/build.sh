#!/bin/sh
# build.sh <NeoAstraPurchases.swift> <output.xcframework>: builds the StoreKit 2 bridge of the purchases plugin as a dynamic
# framework for iOS devices and simulators, which an iOS application embeds, and gathers them in an XCFramework.
set -eu
source="$1"
output="$2"
minimum=15.0
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

framework() { # <sdk> <directory> <target...>
    sdk="$1"; directory="$work/$2/NeoAstraPurchases.framework"; shift 2
    mkdir -p "$directory"
    binaries=""
    for target in "$@"; do
        xcrun --sdk "$sdk" swiftc -target "$target" -sdk "$(xcrun --sdk "$sdk" --show-sdk-path)" -swift-version 5 -O \
            -emit-library -module-name NeoAstraPurchases \
            -Xlinker -install_name -Xlinker @rpath/NeoAstraPurchases.framework/NeoAstraPurchases \
            -o "$work/$target" "$source"
        binaries="$binaries $work/$target"
    done
    # shellcheck disable=SC2086
    xcrun lipo -create $binaries -output "$directory/NeoAstraPurchases"
    platform=$([ "$sdk" = iphoneos ] && echo iPhoneOS || echo iPhoneSimulator)
    cat > "$directory/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
	<key>CFBundleExecutable</key><string>NeoAstraPurchases</string>
	<key>CFBundleIdentifier</key><string>com.neoastra.purchases</string>
	<key>CFBundleName</key><string>NeoAstraPurchases</string>
	<key>CFBundlePackageType</key><string>FMWK</string>
	<key>CFBundleShortVersionString</key><string>1.0</string>
	<key>CFBundleVersion</key><string>1</string>
	<key>CFBundleSupportedPlatforms</key><array><string>$platform</string></array>
	<key>MinimumOSVersion</key><string>$minimum</string>
</dict>
</plist>
PLIST
}

framework iphoneos device arm64-apple-ios$minimum
framework iphonesimulator simulator arm64-apple-ios$minimum-simulator x86_64-apple-ios$minimum-simulator
rm -rf "$output"
xcodebuild -create-xcframework -framework "$work/device/NeoAstraPurchases.framework" -framework "$work/simulator/NeoAstraPurchases.framework" -output "$output" >/dev/null

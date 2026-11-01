#!/usr/bin/env bash
# Builds a mobile sample, installs it, and starts it: on the booted iOS simulator, on a connected iPhone, or on the Android
# device or emulator that adb sees. It finds the bundle or package identifier in what it built, so it serves any project.
#
# Usage: ./run.sh ios-simulator <project> [simulator-udid]
#        ./run.sh ios-device <project> [device-udid]      (signed with the "Apple Development" identity and the profile that Xcode
#                                                          manages for the bundle, which must include the device)
#        ./run.sh android <project> [adb-serial]
# Example: ./run.sh android NeoAstra.Sample.Hello
set -euo pipefail
target=${1:-}; project=${2:-}; device=${3:-}
[[ -n $target && -n $project ]] || { sed -n '2,10p' "$0" >&2; exit 2; }
cd "$(dirname "$0")"
csproj=$(ls "$project"/*.csproj)
# A project for both platforms names the target framework; one for a single platform has only it.
framework() { grep -q "<TargetFrameworks>" "$csproj" && echo "-f $1" || true; }

case $target in
  ios-simulator|ios-device)
    if [[ $target == ios-simulator ]]; then rid=iossimulator-arm64; signing=(); else rid=ios-arm64; signing=(-p:CodesignKey="Apple Development"); fi
    dotnet build "$project" $(framework net11.0-ios) -c Release -r $rid -p:ValidateXcodeVersion=false ${signing[@]+"${signing[@]}"}
    app=$(ls -d "$project"/bin/Release/net11.0-ios/$rid/*.app | head -1)
    id=$(/usr/libexec/PlistBuddy -c "Print CFBundleIdentifier" "$app/Info.plist")
    if [[ $target == ios-simulator ]]; then
      sim=${device:-booted}
      xcrun simctl install "$sim" "$app"
      xcrun simctl launch --terminate-running-process "$sim" "$id"
    else
      if [[ -z $device ]]; then
        device=$(xcrun devicectl list devices 2>/dev/null | awk '/connected/ && !/Simulator/ { for (i = 1; i <= NF; i++) if ($i ~ /^[0-9A-F]{8}-[0-9A-F]{16}$/) { print $i; exit } }')
        [[ -n $device ]] || { echo "No connected iPhone; pass its UDID." >&2; exit 2; }
      fi
      xcrun devicectl device install app --device "$device" "$app"
      # A locked device installs the application but does not start it.
      xcrun devicectl device process launch --device "$device" "$id" || echo "Installed $id; unlock the device to start it." >&2
    fi
    ;;
  android)
    serial=(); [[ -n $device ]] && serial=(-s "$device")
    # The assemblies go into the package, so that adb installs it without the fast deployment of a debug build.
    dotnet build "$project" $(framework net11.0-android) -c Release -r android-arm64 -p:EmbedAssembliesIntoApk=true
    apk=$(ls "$project"/bin/Release/net11.0-android/android-arm64/*-Signed.apk | head -1)
    id=$(basename "$apk" -Signed.apk)
    # A package signed with another key, such as an earlier build of another machine, is replaced.
    adb ${serial[@]+"${serial[@]}"} install -r "$apk" || { adb ${serial[@]+"${serial[@]}"} uninstall "$id"; adb ${serial[@]+"${serial[@]}"} install "$apk"; }
    # A device that has not been unlocked since it started offers no activity to start.
    adb ${serial[@]+"${serial[@]}"} shell monkey -p "$id" -c android.intent.category.LAUNCHER 1 >/dev/null || echo "Installed $id; unlock the device to start it." >&2
    ;;
  *) sed -n '2,10p' "$0" >&2; exit 2 ;;
esac

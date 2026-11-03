# NeoAstra mobile samples (preview)

`NeoAstra.Sample.Hello` is the conventional application of NeoAstra on a phone: one project for iOS and Android, with the page
in `frontend/`, which the frontend pipeline of NeoAstra prepares and packages as on the desktop, and the application in
`HelloApp.cs`, which `NeoApp.AttachAsync` shows from the scene delegate on iOS and from the activity on Android.

`NeoAstra.Sample.iOS` and `NeoAstra.Sample.Android` compose the host by hand instead: a Hello page that calls one generated RPC service (`greeting.hello`) and the desktop services of NeoAstra through
`createDesktopClient`, the location of the device through the [location plugin](../../doc/desktop-services.md#location-plugin), and push messages through the [push plugin](../../doc/desktop-services.md#push-plugin) (on iOS the simulator build has the `aps-environment` entitlement, a device build asks for it with `-p:NeoAstraSamplePush=true` and a provisioning profile with push, and `xcrun simctl push <device> dev.neoastra.sample.mobile payload.json` sends a message; on Android put the `google-services.json` of a Firebase project with an Android application of the same `ApplicationId` beside the project, which git ignores, and send a test message from the Firebase console to the token that the page shows), and passkeys through the [passkeys plugin](../../doc/desktop-services.md#passkeys-plugin) (the page creates the options for the relying party domain typed in its field, which must list the application: an iOS build names it with `-p:NeoAstraSamplePasskeyDomain=<domain>` for its associated domains entitlement, and an Android build needs the package and signing certificate that the `assetlinks.json` of the domain lists), and in-app purchases through the [purchases plugin](../../doc/desktop-services.md#purchases-plugin) (the page loads and buys the product whose identifier and kind are typed in its fields, which the store of the `ApplicationId` sells), hosted on iOS and on Android with the same `MobileHost`. The capability document
`Shared/capabilities/main.json` grants the page exactly the permissions it uses on `ios` and `android`, with scopes, and a
service that a platform does not offer answers `Unsupported`. The host: the application attaches NeoAstra to the run loop of
the platform, creates an environment with the `app` custom scheme, creates a window of NeoAstra (the `UIWindow` of the scene on
iOS, the content of the activity on Android) with the page in it, so that the window commands work too, and recreates the
view when the platform ends its web content process. A later activity on Android, after back closed the previous window,
takes a new window in the same application.

The samples need the .NET 11 SDK with the `ios` and `android` workloads, which `global.json` selects. The native libraries of
the four mobile targets are checked in, as CI builds them; after a change of the native code, `eng/build_native.py` builds and
stages them again:

```sh
python eng/build_native.py --rid iossimulator-arm64
ANDROID_NDK_HOME=/path/to/ndk python eng/build_native.py --rid android-arm64
```

From this folder:

```sh
dotnet build NeoAstra.Sample.Hello -f net11.0-ios -c Release -r iossimulator-arm64
dotnet build NeoAstra.Sample.Hello -f net11.0-android -c Release -r android-arm64
dotnet build NeoAstra.Sample.iOS -c Release -r iossimulator-arm64
dotnet build NeoAstra.Sample.Android -c Release -r android-arm64
```

`run.sh` builds one of them, installs it, and starts it: `./run.sh ios-simulator NeoAstra.Sample.Hello` on the booted simulator,
`./run.sh ios-device NeoAstra.Sample.iOS` on a connected iPhone (signed with the `Apple Development` identity and the profile
that Xcode manages for the bundle, which must include the device), and `./run.sh android NeoAstra.Sample.Android` on the device
or emulator that adb sees; a third argument names the simulator, the iPhone, or the adb serial. It reads the bundle or package
identifier from what it built, so an application of its own can take a copy of it.

They reference the NeoAstra projects and import the targets that the `NeoAstra.Core` package gives a mobile application
(and `NeoAstra.Sample.Android` those of the location plugin);
see [building](../../doc/building.md) for what those targets do and for what the mobile backends do not offer yet. These
samples are not part of `src/NeoAstra.slnx`, so the desktop build needs no mobile workload.

## Trying it on a device

- **Android**: a phone with USB debugging that `adb devices` lists; `./run.sh android NeoAstra.Sample.Android` builds a release
  build signed with the debug key of the SDK, installs it, and starts it. Push needs a `google-services.json` of a Firebase
  project of your own next to the project; without one, the push card answers `Unsupported` and everything else works.
- **iOS simulator**: `./run.sh ios-simulator NeoAstra.Sample.iOS` on the booted simulator, push included (`xcrun simctl push`).
- **iPhone**: `./run.sh ios-device NeoAstra.Sample.iOS`, signed with your `Apple Development` identity and the team profile that
  Xcode manages, which the iPhone must be in. A build for a device leaves push out, which a wildcard profile does not allow;
  `-p:NeoAstraSamplePush=true` adds it for a profile of the bundle with push. Passkeys and purchases need a domain and products
  of your own (`NeoAstraSamplePasskeyDomain`, the product field of the card).

## Checking that it works

Every card calls one capability and shows the status and the answer of the host; a green badge is a success. These cards check
behavior that a quick look does not show:

| Card | What to do | What happens |
| --- | --- | --- |
| Link: tap versus script | Tap the link. Then tap the button and wait six seconds | The tap opens GitHub in the browser and the card reports `OpenExternal`, user-initiated. The script's click opens nothing and reports `Cancel`, not user-initiated |
| Clipboard HTML | Copy HTML, then Paste HTML; also copy a part of a web page in Safari or Chrome first | The HTML comes back, also when the browser kept it as data |
| History off | Open a second page, tap `history.back()` | The page stays: the view refuses the documents of its history |
| Notifications FB and Ea | Show both, tap each in the notification center | The Reminder card reports `Activated` with the ID and the payload of the one tapped: `FB` brings `FB`, `Ea` brings `Ea`, although the two IDs have the same hash code in Java |
| Keyboard | Tap the field | The keyboard lies over the page, the card reports its height and the duration of its animation, and the page moves the field above it |
| Screenshot | Capture, then This card | The host captures the view as an automation tool would: first the visible page, then only this card, whose image is its size in CSS pixels times the device pixel ratio |
| Reminder | Ask, then In 10 s, and close the application | The notification comes after ten seconds; tapping it starts the application, and the card reports the tap as `Launched` |
| Back button (Android) | Open a layer twice, then press back three times | Back closes the two layers first, then the window |
| Window color | Dark, then Light | The window behind the page and, on Android, the symbols of the system bars follow |
| Displays | Read | The screen of the phone with its size, scale, and safe area |

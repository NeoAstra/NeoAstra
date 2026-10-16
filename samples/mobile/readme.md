# NeoAstra mobile samples (preview)

`NeoAstra.Sample.Hello` is the conventional application of NeoAstra on a phone: one project for iOS and Android, with the page
in `frontend/`, which the frontend pipeline of NeoAstra prepares and packages as on the desktop, and the application in
`HelloApp.cs`, which `NeoApp.AttachAsync` shows from the scene delegate on iOS and from the activity on Android.

`NeoAstra.Sample.iOS` and `NeoAstra.Sample.Android` compose the host by hand instead: a Hello page that calls one generated RPC service (`greeting.hello`) and the desktop services of NeoAstra through
`createDesktopClient`, and the location of the device through the [location plugin](../../doc/desktop-services.md#location-plugin), hosted on iOS and on Android with the same `MobileHost`. The capability document
`Shared/capabilities/main.json` grants the page exactly the permissions it uses on `ios` and `android`, with scopes, and a
service that a platform does not offer answers `Unsupported`. The host: the application attaches NeoAstra to the run loop of
the platform, creates an environment with the `app` custom scheme, creates a window of NeoAstra (the `UIWindow` of the scene on
iOS, the content of the activity on Android) with the page in it, so that the window commands work too, and recreates the
view when the platform ends its web content process. A later activity on Android, after back closed the previous window,
takes a new window in the same application.

The samples need the .NET 11 SDK with the `ios` and `android` workloads, which `global.json` selects, and the native libraries
of the targets, which `eng/build_native.py` builds and stages:

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

They reference the NeoAstra projects and import the targets that the `NeoAstra.Core` package gives a mobile application
(and `NeoAstra.Sample.Android` those of the location plugin);
see [building](../../doc/building.md) for what those targets do and for what the mobile backends do not offer yet. These
samples are not part of `src/NeoAstra.slnx`, so the desktop build needs no mobile workload.

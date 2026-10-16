# NeoAstra mobile samples (preview)

A Hello page that calls one generated RPC service (`greeting.hello`) and the desktop services of NeoAstra through
`createDesktopClient`, hosted on iOS and on Android with the same `MobileHost`. The capability document
`Shared/capabilities/main.json` grants the page exactly the permissions it uses on `ios` and `android`, with scopes, and a
service that a platform does not offer answers `Unsupported`, a window command answers that it needs a desktop window. The
host:
the application attaches NeoAstra to the run loop of the platform, creates an environment with the `app` custom scheme, shows
a view in a native view of its own (`NeoAstraHost.FromUIKitUIView` or `NeoAstraHost.FromAndroidViewGroup`), and recreates the
view when the platform ends its web content process.

The samples need the .NET 11 SDK with the `ios` and `android` workloads, which `global.json` selects, and the native libraries
of the targets, which `eng/build_native.py` builds and stages:

```sh
python eng/build_native.py --rid iossimulator-arm64
ANDROID_NDK_HOME=/path/to/ndk python eng/build_native.py --rid android-arm64
```

From this folder:

```sh
dotnet build NeoAstra.Sample.iOS -c Release -r iossimulator-arm64
dotnet build NeoAstra.Sample.Android -c Release -r android-arm64
```

They reference the NeoAstra projects and import the targets that the `NeoAstra.Core` package gives a mobile application;
see [building](../../doc/building.md) for what those targets do and for what the mobile backends do not offer yet. These
samples are not part of `src/NeoAstra.slnx`, so the desktop build needs no mobile workload.

# NeoWebView [![ci](https://github.com/xoofx/NeoWebView/actions/workflows/ci.yml/badge.svg)](https://github.com/xoofx/NeoWebView/actions/workflows/ci.yml) [![NuGet](https://img.shields.io/nuget/v/NeoWebView.svg)](https://www.nuget.org/packages/NeoWebView/)

<img align="right" width="160px" height="160px" src="https://raw.githubusercontent.com/xoofx/NeoWebView/main/img/NeoWebView.png">

Build native desktop applications with .NET and web technologies using the platform browser: WebView2 on Windows, WKWebView on macOS, and WebKitGTK on Linux.

> NeoWebView is pre-release software. The portable core and Windows vertical slice are usable; some advanced browser features and cross-platform validation are still in progress.

## Features

- Native top-level windows or embedding into a borrowed `HWND`, `NSView`, or `GtkWidget`
- Mutable window bounds, size constraints, and normal/minimized/maximized/fullscreen state
- Asynchronous environment, profile, and browser-view creation
- Profile cookie management and selective browsing-data clearing
- Navigation state and completion events
- Browser/web-process termination reporting with portable recovery guidance
- JavaScript evaluation and persistent document scripts
- Portable page zoom control
- JSON web/native messaging
- Deferred navigation, popup, and permission decisions with safe defaults
- Typed native handles and runtime capability discovery
- Generated, size/versioned C ABI interop with explicit lifetime management

## Quick start

```csharp
using NeoWebView;

return NeoApplication.Run(
    new NeoApplicationOptions { ApplicationName = "NeoWebView Sample" },
    async app =>
    {
        var window = app.CreateWindow(new NeoWindowOptions
        {
            Title = "NeoWebView",
            Width = 1000,
            Height = 700,
            IsVisible = true,
        });

        await using var environment = await app.CreateEnvironmentAsync();
        await using var webView = await environment.CreateWebViewAsync(
            NeoWebViewHost.FillWindow(window));

        webView.NavigationCompleted += (_, navigation) =>
            Console.WriteLine($"Navigation succeeded: {navigation.IsSuccess}");

        await webView.NavigateAsync(new Uri("https://example.com/"));
    });
```

NeoWebView application and browser operations must begin on the platform UI thread. `NeoApplication.Run` installs a dispatcher synchronization context so continuations return to that thread. On Windows, an attached host thread must use an STA apartment.

An embedded host created with `NeoApplication.AttachToCurrentThread` must await `DisposeAsync` while its owning UI loop is still pumping. Disposal marshals explicit detach to that thread, rejects new work, drains accepted dispatcher callbacks, and completes child-before-application platform teardown. Native hosts must call `neo_webview_app_detach` on the owning UI thread before stopping their loop. Final release from another thread only requests UI teardown; if the host has already stopped pumping, NeoWebView intentionally leaves that application pending rather than running COM, Cocoa, or GTK teardown on the wrong thread.

## Building

Managed projects target .NET 10:

Managed and RID-specific native assets are a paired release unit. The managed loader rejects a native ABI major or minor mismatch rather than supporting mixed NeoWebView releases.

```sh
cd src
dotnet build -c Release
dotnet test -c Release
```

The native library uses CMake presets and Clang. The build helper selects a .NET RID, runs the native tests, and stages the resulting library in `src/NeoWebView/runtimes/<RID>/native`:

```sh
python eng/build_native.py --rid win-x64 --clean
```

The managed project directly copies the staged library for the current host RID beside its development output, so local applications and tests use the latest native build without creating or installing a NuGet package. Rerun the helper after native changes. The same command accepts `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, and `osx-arm64`; use `--skip-tests` when cross-compiling a binary that cannot run on the build host.

See [`doc/neowebview_specs.md`](doc/neowebview_specs.md) for the architecture and normative implementation requirements.

## License

This software is released under the [BSD-2-Clause license](https://opensource.org/licenses/BSD-2-Clause).

## Author

Alexandre Mutel aka [xoofx](https://xoofx.github.io).

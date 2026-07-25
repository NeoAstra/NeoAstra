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
- JavaScript evaluation and persistent document scripts
- JSON web/native messaging
- Deferred policy decisions with safe defaults
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

## Building

Managed projects target .NET 10:

```sh
cd src
dotnet build -c Release
dotnet test -c Release
```

The native library uses CMake presets and Clang. For example, on Windows:

```sh
cmake --preset windows-x64-debug
cmake --build --preset windows-x64-debug
ctest --preset windows-x64-debug
```

See [`doc/neowebview_specs.md`](doc/neowebview_specs.md) for the architecture and normative implementation requirements.

## License

This software is released under the [BSD-2-Clause license](https://opensource.org/licenses/BSD-2-Clause).

## Author

Alexandre Mutel aka [xoofx](https://xoofx.github.io).

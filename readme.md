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
- Windows custom schemes and directory-backed local application assets without a localhost server
- Deferred browser decisions with timeout-safe defaults, including navigation, permissions, dialogs, authentication, certificates, and fullscreen where supported
- Tracked opener-compatible popup views hosted by normal application windows or borrowed parents
- Download destination/default/cancel policy plus tracked lifecycle, progress, cancellation, and Windows pause/resume
- Portable file chooser decisions on macOS and Linux
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

On Windows, register local application content before creating the environment. The directory provider rejects encoded traversal, links/reparse points, and files outside its fixed root; it serves only `GET` and `HEAD` requests. Application schemes are secure, authority-based origins and are automatically trusted for the message bridge:

```csharp
var assets = new NeoDirectoryResourceProvider(Path.Combine(AppContext.BaseDirectory, "assets"));
await using var environment = await app.CreateEnvironmentAsync(new NeoEnvironmentOptions
{
    CustomSchemes = [NeoCustomScheme.Application("app", assets)],
});
await using var webView = await environment.CreateWebViewAsync(NeoWebViewHost.FillWindow(window));
await webView.NavigateAsync(new Uri("app://neowebview/index.html"));
```

Custom resource-provider callbacks are synchronous because WebView2 requests the response synchronously. Return `null` for a standard `404`, use `NeoResourceResponse.FromBytes` for small generated content, or `NeoResourceResponse.FromFile`/`NeoDirectoryResourceProvider` to avoid copying local files into managed memory. Provider exceptions are contained at the ABI boundary and the request fails rather than unwinding into native code. On Windows, web messaging is blocked for untrusted remote origins unless they are explicitly listed in `NeoWebViewOptions.BridgeOrigins`.

An embedded host created with `NeoApplication.AttachToCurrentThread` must await `DisposeAsync` while its owning UI loop is still pumping. Disposal marshals explicit detach to that thread, rejects new work, cancels accepted managed dispatcher waits that have not started, drains their native callbacks, and completes child-before-application platform teardown. Native hosts must call `neo_webview_app_detach` on the owning UI thread before stopping their loop. Final release from another thread only requests UI teardown; if the host has already stopped pumping, NeoWebView intentionally leaves that application pending rather than running COM, Cocoa, or GTK teardown on the wrong thread.

Native diagnostics can be observed without an additional logging dependency by setting `NeoApplicationOptions.LogCallback`. The callback can run on any native thread; its `NeoLogMessage` includes severity, category, UTF-8 message, native thread identifier, monotonic timestamp, optional native code, and object identifier. Exceptions thrown by the callback are contained at the managed/native boundary.

Use `NeoEnvironment.GetCapability` before enabling optional browser UX. WebView2 does not expose portable file-chooser interception, WebKitGTK does not expose the current TLS/client-certificate decision hooks, and WKWebView does not expose the portable client-certificate or fullscreen hooks.

The basic sample is configured for NativeAOT. Publish it for the current platform, for example with `dotnet publish samples/NeoWebView.Sample/NeoWebView.Sample.csproj -c Release -r win-x64 --self-contained`. Passing `--validate-native-library` performs a non-interactive native load and dispatcher-detach smoke check without creating a browser view; CI runs that check against the freshly built Windows native asset.

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

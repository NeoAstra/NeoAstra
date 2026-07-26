# NeoAstra [![ci](https://github.com/xoofx/NeoAstra/actions/workflows/ci.yml/badge.svg)](https://github.com/xoofx/NeoAstra/actions/workflows/ci.yml) [![NuGet](https://img.shields.io/nuget/v/NeoAstra.svg)](https://www.nuget.org/packages/NeoAstra/)

<img align="right" width="160px" height="160px" src="https://raw.githubusercontent.com/xoofx/NeoAstra/main/img/NeoAstra.png">

Build native desktop applications with .NET and web technologies using the platform browser: WebView2 on Windows, WKWebView on macOS, and WebKitGTK on Linux.

> NeoAstra is pre-release software. The portable core and Windows vertical slice are usable; some advanced browser features and cross-platform validation are still in progress.

See [platform support and runtime dependencies](doc/platform-support.md) for the distinction between v1 support intent, implemented backends, configured workflow coverage, and runtime validation. Review the [known limitations](doc/known-limitations.md) before shipping an application.

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
- Framework-neutral `@neoastra/client` ESM transport with version negotiation, document-session teardown, diagnostics, and deterministic mocks
- Cross-platform custom schemes and directory-backed local application assets without a localhost server
- Deferred browser decisions with timeout-safe defaults, including navigation, permissions, dialogs, authentication, certificates, and fullscreen where supported
- Tracked opener-compatible popup views hosted by normal application windows or borrowed parents
- Download destination/default/cancel policy plus tracked lifecycle, progress, cancellation, and Windows pause/resume
- Portable file chooser decisions on macOS and Linux
- Typed native handles and runtime capability discovery
- Generated, size/versioned C ABI interop with explicit lifetime management

## Quick start

```csharp
using NeoAstra;

return NeoApplication.Run(
    new NeoApplicationOptions { ApplicationName = "NeoAstra Sample" },
    async app =>
    {
        var window = app.CreateWindow(new NeoWindowOptions
        {
            Title = "NeoAstra",
            Width = 1000,
            Height = 700,
            IsVisible = true,
        });

        await using var environment = await app.CreateEnvironmentAsync();
        await using var webView = await environment.CreateWebViewAsync(
            NeoAstraHost.FillWindow(window));

        webView.NavigationCompleted += (_, navigation) =>
            Console.WriteLine($"Navigation succeeded: {navigation.IsSuccess}");

        await webView.NavigateAsync(new Uri("https://example.com/"));
    });
```

NeoAstra application and browser operations must begin on the platform UI thread. `NeoApplication.Run` installs a dispatcher synchronization context so continuations return to that thread. On Windows, an attached host thread must use an STA apartment.

Register local application content before creating the environment. The directory provider rejects encoded traversal, links/reparse points, and files outside its fixed root; it serves only `GET` and `HEAD` requests. Application-scheme descriptors are authority-based and marked secure. Bridge access is separate and default-denied. For a cross-platform, locked-down local view, explicitly opt into whole-view trust only when every document, frame, script, asset, and navigation is controlled:

```csharp
var assets = new NeoDirectoryResourceProvider(Path.Combine(AppContext.BaseDirectory, "assets"));
await using var environment = await app.CreateEnvironmentAsync(new NeoEnvironmentOptions
{
    CustomSchemes = [NeoCustomScheme.Application("app", assets)],
});
var viewOptions = new NeoAstraOptions
{
    BridgePolicy = NeoBridgePolicy.TrustEntireView,
};
await using var webView = await environment.CreateWebViewAsync(
    NeoAstraHost.FillWindow(window),
    viewOptions);
await webView.NavigateAsync(new Uri("app://neoastra/index.html"));
```

Custom resource-provider callbacks currently remain synchronous on all three backends: WebView2 requests its response synchronously, WKWebView completes the scheme task directly from `startURLSchemeTask`, and WebKitGTK completes `WebKitURISchemeRequest` from its registered callback. Return `null` for a standard `404`, use `NeoResourceResponse.FromBytes` for generated content up to the 64 MiB buffered-body limit, or `NeoResourceResponse.FromFile`/`NeoDirectoryResourceProvider` to avoid copying larger local files into managed memory. Windows uses a native file stream, macOS uses native `NSData` with mapped-if-safe file access, and Linux opens a `GFileInputStream`; byte responses are copied into backend-owned memory before the managed response lease is released. Provider exceptions are contained at the ABI boundary and the request fails rather than unwinding into native code.

`TrustEntireView` trusts every script that can reach the registered handler. Remote navigation, iframes, remote script dependencies, mutable assets, injection flaws, or an ineffective CSP can therefore expose bridge authority. On Windows and macOS, applications that permit navigation outside fully controlled content should instead set `BridgePolicy = NeoBridgePolicy.TrustedOrigins` with a non-empty exact `BridgeOrigins` allowlist. An empty list never means allow-all.

WebKitGTK exposes URI, method, headers, and a synchronously buffered request body (limited to 64 MiB), but not trustworthy initiating-origin, frame, or resource-kind metadata for these requests; those fields are reported as unknown. Linux honors secure and CORS-enabled scheme flags, but has no equivalent authority or per-origin CORS registration switches and rejects service-worker descriptors, so custom-scheme capability is reported as limited. WebKitGTK 4.1 script-message callbacks also omit trustworthy source-origin data. NeoAstra therefore does not infer trust from the current top-level URI: `TrustedOrigins` is rejected on Linux, `TrustEntireView` delivers messages with `SourceOrigin == null`, and message-origin capability remains unavailable.

See [the security and resource-limit review](doc/security-review.md) for the verified controls, trust assumptions, and backend limitations.
See [the portable frontend transport and migration guide](doc/frontend-transport.md) before enabling
v2 frontend messaging. Bridge-enabled views require a unique `ViewLabel`; application frontend code
uses `@neoastra/client` and never selects backend browser globals.
See [the typed RPC and generated bindings guide](doc/rpc-and-bindings.md) for explicit NativeAOT-safe
commands, cancellation, events, channels, resources, deterministic artifacts, and test doubles.

An embedded host created with `NeoApplication.AttachToCurrentThread` must await `DisposeAsync` while its owning UI loop is still pumping. Disposal marshals explicit detach to that thread, rejects new work, cancels accepted managed dispatcher waits that have not started, drains their native callbacks, and completes child-before-application platform teardown. Native hosts must call `neoastra_app_detach` on the owning UI thread before stopping their loop. Final release from another thread only requests UI teardown; if the host has already stopped pumping, NeoAstra intentionally leaves that application pending rather than running COM, Cocoa, or GTK teardown on the wrong thread.

Native diagnostics can be observed without an additional logging dependency by setting `NeoApplicationOptions.LogCallback`. The callback can run on any native thread; its `NeoLogMessage` includes severity, category, UTF-8 message, native thread identifier, monotonic timestamp, optional native code, and object identifier. Exceptions thrown by the callback are contained at the managed/native boundary.

Use `NeoEnvironment.GetCapability` before enabling optional browser UX. WebView2 does not expose portable file-chooser interception, WebKitGTK does not expose the current TLS/client-certificate decision hooks, and WKWebView does not expose the portable client-certificate or fullscreen hooks.

The basic sample is configured for NativeAOT. Publish it for the current platform, for example with `dotnet publish samples/NeoAstra.Sample/NeoAstra.Sample.csproj -c Release -r win-x64 --self-contained`. Passing `--validate-native-library` performs a non-interactive native load and dispatcher-detach smoke check without creating a browser view; CI runs that check against the freshly built Windows native asset.

## Building

Managed projects target .NET 10:

Managed and RID-specific native assets are a paired release unit. The managed loader rejects a native ABI major or minor mismatch rather than supporting mixed NeoAstra releases.

```sh
cd src
dotnet build -c Release
dotnet test -c Release
```

The browser conformance and performance executables are built with the solution but are never run by a normal build or test. Both are noninteractive and opt-in; invoking either without arguments exits successfully without creating an application or browser. From the repository root, run local conformance with `dotnet run --project src/NeoAstra.Conformance -c Release -- --run`. Add `--stress` for the bounded high-volume scenarios or `--timeout-seconds N` to change the per-scenario limit. The harness uses only copied `conformance://` fixtures and prints an explicit `SKIP` when a backend capability, trusted user activation, destructive process failure, filesystem mutation, or subprocess isolation prevents safe automation.

Run the dependency-free benchmark harness with `dotnet run --project src/NeoAstra.Benchmarks -c Release -- --run --quick`; omit `--quick` for the bounded default sample, or use `--iterations`, `--lifecycle-iterations`, `--timeout-seconds`, and `--idle-seconds` to tune it. Each `RESULT`/`SKIP` identifies the backend and platform. Results include browser-engine, native-backend, OS-scheduling, and machine effects; environment/view timing must not be interpreted as NeoAstra controlling engine startup, and memory/idle-CPU figures currently cover only the host process. Use same-machine, same-engine regression baselines rather than absolute comparisons across platforms.

The native library uses CMake presets and Clang. The build helper selects a .NET RID, runs the native tests, and stages the resulting library in `src/NeoAstra/runtimes/<RID>/native`:

```sh
python eng/build_native.py --rid win-x64 --clean
```

Build and verify the frontend package, CSP/bootstrap fixtures, framework consumers, size budget,
licenses, provenance, and publish contents with:

```sh
cd frontend
npm ci
npm run check
```

The managed project directly copies the staged library for the current host RID beside its development output, so local applications and tests use the latest native build without creating or installing a NuGet package. Rerun the helper after native changes. The same command accepts `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, and `osx-arm64`; use `--skip-tests` when cross-compiling a binary that cannot run on the build host.

Native tests include a public-header ABI test, common ownership tests, contended dispatch/UI-object teardown stress tests, and an independent frozen ABI 1.7 consumer. The frozen consumer does not include the current header: it loads the shared library by path, verifies every ABI 1.7 export, calls core entry points through frozen C-layout declarations, and performs an attach/detach ownership cycle. Compatible exports added after ABI 1.7 do not change that export floor.

Native CI is configured with explicit `linux-x64-asan-ubsan`, `linux-x64-tsan`, `linux-x64-analysis`, and `macos-x64-asan-ubsan` presets. The ThreadSanitizer test preset selects only the common ownership and contended teardown tests; it does not run browser conformance automation. Sanitizer test presets use fail-fast runtime options, instrument the test executables and shared library, and disable LTO. LeakSanitizer remains disabled until process-global allocations in GTK/WebKitGTK and Apple WebKit have reviewed suppressions. These are configured CI jobs, not evidence that they ran on a Windows development host; run the matching preset on its named host to establish an execution result. ThreadSanitizer remains separate from AddressSanitizer and UndefinedBehaviorSanitizer.

See [`doc/neoastra_specs.md`](doc/neoastra_specs.md) for the current architecture and normative v1 implementation requirements. The planned application-platform evolution is specified by [`doc/neoastra_v2_specs.md`](doc/neoastra_v2_specs.md) and its implementation-step sub-specifications.

## License

This software is released under the [BSD-2-Clause license](https://opensource.org/licenses/BSD-2-Clause).

Release artifacts also require the applicable [third-party notices](THIRD-PARTY-NOTICES.md).

## Author

Alexandre Mutel aka [xoofx](https://xoofx.github.io).

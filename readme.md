# NeoAstra [![ci](https://github.com/NeoAstra/NeoAstra/actions/workflows/ci.yml/badge.svg)](https://github.com/NeoAstra/NeoAstra/actions/workflows/ci.yml) [![NuGet](https://img.shields.io/nuget/v/NeoAstra.svg)](https://www.nuget.org/packages/NeoAstra/)

<img align="right" width="160px" height="160px" src="https://raw.githubusercontent.com/NeoAstra/NeoAstra/main/img/NeoAstra.png">

Build native desktop applications with .NET and web technologies using the platform browser: WebView2 on Windows, WKWebView on macOS, and WebKitGTK on Linux.

> [!WARNING]
> NeoAstra is under construction and not yet available.

See [platform support and runtime dependencies](doc/platform-support.md) for the distinction between v1 support intent, implemented backends, configured workflow coverage, and runtime validation. Review the [known limitations](doc/known-limitations.md) before shipping an application.

## Features

- Native top-level windows or embedding into a borrowed `HWND`, `NSView`, or `GtkWidget`
- Mutable window bounds, constraints, decorations, resizing, topmost/task-switcher behavior, and normal/minimized/maximized/fullscreen state
- Typed window transition events plus native chromeless drag and resize interactions
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
- Async cancelable close/quit coordination, bounded launch routing, secure local-user single instance, and optional Generic Host integration

## Product packages

| Package | Role | Application runtime reference |
| --- | --- | --- |
| `NeoAstra.Core` | Cross-platform window and WebView core; public API remains in namespace `NeoAstra` | Use directly for low-level hosts |
| `NeoAstra` | Default desktop application platform: RPC, capabilities, desktop services, hosting, embedded generator, and frontend build integration | The single reference for ordinary apps; depends on `NeoAstra.Core` |
| `NeoAstra.Tool` | Optional `dotnet neoastra` development, capability, asset, delivery, and update tooling | Tool installation only; not a runtime framework package |
| `NeoAstra.Templates` | Vanilla, React, and Vue `dotnet new` templates | Template installation only |

NeoAstra has not shipped a stable release. Earlier repository-only package and project boundaries were removed as a clean break; there are no compatibility packages or migration shims.

## Quick start

Install one package for the complete application platform:

```xml
<PackageReference Include="NeoAstra" Version="1.0.0" />
```

The package includes RPC, secure capabilities, desktop services, hosting integration, the incremental RPC generator, and frontend build targets. Use `NeoAstra.Core` instead when you want only the low-level cross-platform WebView/window API; its public types remain in the `NeoAstra` namespace.

```csharp
using System;
using System.Text.Json.Serialization;
using NeoAstra;
using NeoAstra.Rpc;

[assembly: NeoRpcJsonContext(typeof(AppJsonContext))]

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        return NeoApp.Run(args, app =>
        {
            app.UseRpc(rpc => rpc.AddGreetingService(new GreetingService()));
            app.GrantMainView("greeting:read"); // authority is always explicit
        });
    }
}

[NeoRpcService("greeting")]
sealed class GreetingService
{
    [NeoRpcMethod("hello", Permission = "greeting:read")]
    public ValueTask<GreetingResponse> HelloAsync(GreetingRequest request) =>
        ValueTask.FromResult(new GreetingResponse($"Hello, {request.Name}!"));
}

sealed record GreetingRequest(string Name);
sealed record GreetingResponse(string Message);

[JsonSerializable(typeof(GreetingRequest))]
[JsonSerializable(typeof(GreetingResponse))]
partial class AppJsonContext : JsonSerializerContext;
```

`NeoApp` creates a secure one-window local application, serves manifest-backed `assets/`, selects a safe bridge policy for the current platform, binds RPC, and tears resources down deterministically. `NEOASTRA_DEV_URL` accepts only an exact loopback IP origin. Service registration does not grant renderer authority: the `GrantMainView` line is required. See [`samples/NeoAstra.Sample`](samples/NeoAstra.Sample) for the complete HelloWorld, including a generated plain-JavaScript binding whose methods carry the contract hash automatically, and [`samples/NeoAstra.Core.Sample`](samples/NeoAstra.Core.Sample) for direct use of the low-level API.

NeoAstra application and browser operations must begin on the platform UI thread. `NeoApplication.Run` installs a dispatcher synchronization context so continuations return to that thread. On Windows, standalone entry points and attached host threads must use an STA apartment.

## Window management

`NeoWindow` exposes aggregate events for state-oriented code and focused convenience events for ordinary application policy. `BoundsChanged`, `FocusChanged`, and `StateChanged` remain the complete forms; `PositionChanged`, `ClientSizeChanged`, `Activated`, `Deactivated`, `Minimized`, `Maximized`, `Restored`, `FullscreenEntered`, and `FullscreenExited` report distinct native transitions. Duplicate focus and state notifications are suppressed.

Window behavior can be changed after creation, on the application UI thread:

```csharp
window.PositionChanged += (_, e) => SavePosition(e.NewPosition);
window.Activated += (_, _) => RefreshCommands();
window.FullscreenExited += (_, _) => RestoreOverlay();

window.IsResizable = false;
window.HasDecorations = false;
window.IsAlwaysOnTop = true;
window.Maximize();       // also Minimize(), Restore(), EnterFullscreen(), ExitFullscreen()
window.BringToFront();
```

For a custom title bar, call `BeginDrag()` synchronously from a trusted pointer-press path. Custom resize handles call `BeginResize(NeoWindowResizeEdge)`. These methods ask the native window manager to perform the interaction; they do not synthesize input and are not renderer-callable by default. Interactive resizing is available on Windows and Linux; macOS currently reports `NotSupportedException`. Per-window `ShowInTaskbar` changes are likewise unavailable on macOS because Dock membership is application-scoped. On Wayland, compositor-controlled positions and move notifications remain best-effort. See [known limitations](doc/known-limitations.md) for the platform matrix.

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

WebKitGTK exposes URI, method, headers, and a synchronously buffered request body (limited to 64 MiB), but not trustworthy initiating-origin, frame, or resource-kind metadata for these requests; those fields are reported as unknown. Linux honors secure and CORS-enabled scheme flags, but has no equivalent authority or per-origin CORS registration switches and rejects service-worker descriptors, so custom-scheme capability is reported as limited. WebKitGTK 6.0 script-message callbacks also omit trustworthy source-origin data. NeoAstra therefore does not infer trust from the current top-level URI: `TrustedOrigins` is rejected on Linux, `TrustEntireView` delivers messages with `SourceOrigin == null`, and message-origin capability remains unavailable.

See [the security and resource-limit review](doc/security-review.md) for the verified controls, trust assumptions, and backend limitations.
See [the portable frontend transport guide](doc/frontend-transport.md) before enabling
frontend messaging. Bridge-enabled views require a unique `ViewLabel`; application frontend code
uses `@neoastra/client` and never selects backend browser globals.
See [the typed RPC and generated bindings guide](doc/rpc-and-bindings.md) for explicit NativeAOT-safe
commands, cancellation, events, channels, resources, deterministic artifacts, and test doubles.
Before exposing RPC to a renderer, follow [the capability and security guide](doc/capabilities-and-security.md),
including its fail-closed host setup, platform provenance limits, [threat model](doc/security-threat-model.md),
and reviewed capability configuration for advanced/scoped applications.
Use [the frontend tooling, secure assets, and templates guide](doc/frontend-tooling-and-assets.md)
for locked incremental npm restore (including executable Node-manager detection and an explicit invocation override) and framework-neutral frontend preparation during normal `dotnet build`/`dotnet run`, convention-based projects,
optional `neoastra.json` overrides, `dotnet neoastra dev/init/doctor/inspect`, generated-contract ordering, manifest-only SPA hosting,
plain-JavaScript/no-Node and offline/prebuilt workflows, and the vanilla/React/Vue templates.
Use [the application lifecycle and hosting guide](doc/application-lifecycle-and-hosting.md) for async unsaved-work close,
deterministic quit ordering, early launch events, authenticated second-instance routing, explicit DI scopes, and platform session-end limitations.
Use [the plugins and desktop services guide](doc/desktop-services.md) for static plugin composition, renderer authority boundaries,
desktop support/degradation by platform, scoped open/drag operations, safe storage, and recoverable window-state persistence.
Use [the delivery and authenticated update guide](doc/delivery-and-updates.md) for schema-validated deterministic bundles,
inspectable host package inputs, signing adapters, SBOM/provenance, and the experimental fail-closed updater threat model.

An embedded host created with `NeoApplication.AttachToCurrentThread` must await `DisposeAsync` while its owning UI loop is still pumping. Disposal marshals explicit detach to that thread, rejects new work, cancels accepted managed dispatcher waits that have not started, drains their native callbacks, and completes child-before-application platform teardown. Native hosts must call `neoastra_app_detach` on the owning UI thread before stopping their loop. Final release from another thread only requests UI teardown; if the host has already stopped pumping, NeoAstra intentionally leaves that application pending rather than running COM, Cocoa, or GTK teardown on the wrong thread.

Native diagnostics can be observed without an additional logging dependency by setting `NeoApplicationOptions.LogCallback`. The callback can run on any native thread; its `NeoLogMessage` includes severity, category, UTF-8 message, native thread identifier, monotonic timestamp, optional native code, and object identifier. Exceptions thrown by the callback are contained at the managed/native boundary.

Use `NeoEnvironment.GetCapability` before enabling optional browser UX. WebView2 does not expose portable file-chooser interception, WebKitGTK does not expose the current TLS/client-certificate decision hooks, and WKWebView does not expose the portable client-certificate or fullscreen hooks.

Both samples are configured for NativeAOT. Publish the complete HelloWorld with `dotnet publish samples/NeoAstra.Sample/NeoAstra.Sample.csproj -c Release -r win-x64 --self-contained`. The Core sample's `--validate-native-library` option performs a non-interactive native load and dispatcher-detach smoke check without creating a browser view.

For a guided application-platform demonstration, run the
[`NeoAstra.Sample.Advanced` feature tour](samples/NeoAstra.Sample.Advanced/readme.md). It combines a React/Vite
view with generated typed RPC, cancellation, channels, events, differently authorized views, lifecycle
negotiation, native desktop services, secure local assets, and a compact standalone NativeAOT host.

## Building

Managed projects target .NET 10:

NeoAstra's pre-release native ABI remains `1.0`. Until the first release, the managed loader and bundler enforce only ABI major compatibility so existing pre-release RID assets can be regenerated without blocking local applications. Managed and RID-specific native assets will become a strictly paired release unit before release.

```sh
cd src
dotnet build -c Release
dotnet test -c Release
```

The browser conformance and performance executables are built with the solution but are never run by a normal build or test. Both are noninteractive and opt-in; invoking either without arguments exits successfully without creating an application or browser. From the repository root, run local conformance with `dotnet run --project src/NeoAstra.Conformance -c Release -- --run`. Add `--stress` for the bounded high-volume scenarios or `--timeout-seconds N` to change the per-scenario limit. The harness uses only copied `conformance://` fixtures and prints an explicit `SKIP` when a backend capability, trusted user activation, destructive process failure, filesystem mutation, or subprocess isolation prevents safe automation.

Run the dependency-free benchmark harness with `dotnet run --project src/NeoAstra.Benchmarks -c Release -- --run --quick`; omit `--quick` for the bounded default sample, or use `--iterations`, `--lifecycle-iterations`, `--timeout-seconds`, and `--idle-seconds` to tune it. Each `RESULT`/`SKIP` identifies the backend and platform. Results include browser-engine, native-backend, OS-scheduling, and machine effects; environment/view timing must not be interpreted as NeoAstra controlling engine startup, and memory/idle-CPU figures currently cover only the host process. Use same-machine, same-engine regression baselines rather than absolute comparisons across platforms.

The native library uses CMake presets and Clang. The build helper selects a .NET RID, runs the native tests, and stages the resulting library in `src/NeoAstra.Core/runtimes/<RID>/native`:

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

See [`doc/neoastra_specs.md`](doc/neoastra_specs.md) for the original Core architecture. The application-platform design audits under the `neoastra_v2_*` filenames are retained as historical implementation records; their former package and sample names are superseded by the two-package product described above.

## License

This software is released under the [BSD-2-Clause license](https://opensource.org/licenses/BSD-2-Clause).

Release artifacts also require the applicable [third-party notices](THIRD-PARTY-NOTICES.md).

## Author

Alexandre Mutel aka [xoofx](https://xoofx.github.io).

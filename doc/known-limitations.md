# Known limitations

NeoAstra is pre-release software. Public APIs, the native ABI, packaging, and platform behavior may
still change before v1. The [platform support page](platform-support.md) separates intended support,
implemented source, configured workflow coverage, and actual runtime validation.

## Release-readiness gaps

- Release-level browser validation is not established here for macOS or Linux. Windows ARM64 native
  execution and browser validation are also not established.
- The minimum supported macOS version has not been frozen.
- Configured build/package workflows do not by themselves establish release readiness. Each configured
  native RID now assembles a separate readiness artifact containing the staged native binary, public
  headers, separate native debug symbols, runtime documentation, third-party notices, an export-based
  ABI report, and a SHA-256 manifest; the package artifact also receives a SHA-256 manifest. The
  manually dispatched package workflow verifies that the managed symbol package's portable PDB maps
  sources to the checked-out commit in this GitHub repository. Workflow configuration is not evidence
  that those jobs passed, and review of the complete artifact set remains release acceptance work.
- [`THIRD-PARTY-NOTICES.md`](../THIRD-PARTY-NOTICES.md) records the redistributed WebView2 SDK
  material evidenced by the current build. It, this limitations page, the platform-support page, and
  the public native headers are included in the NuGet package.
- Linux musl, all 32-bit targets, and RIDs outside `win-x64`, `win-arm64`, `osx-x64`, `osx-arm64`,
  `linux-x64`, and `linux-arm64` are unsupported.

## Backend capability differences

Applications should query `NeoEnvironment.GetCapability` and provide a safe fallback rather than
assuming that every browser engine supports every portable event.

| Area | Windows / WebView2 | macOS / WKWebView | Linux / WebKitGTK 6.0 |
| --- | --- | --- | --- |
| File-chooser interception | Not exposed by the portable backend | Available | Available |
| Client-certificate decisions | Available | Not exposed | Not exposed |
| TLS-error decisions | Available | Available | Not exposed |
| Fullscreen decisions | Available | Not exposed | Available |
| Download pause/resume | Available when reported by the runtime | Not exposed | Not exposed |
| Trusted message origins | Exact trusted-origin policy available | Exact trusted-origin policy available | Unavailable; sender-origin data is not trustworthy |
| Arbitrary-method top-level navigation | Available | Available | Not exposed; only a plain `GET` without extra headers/body uses portable navigation |
| Chromeless native drag | Available | Available | Available when the compositor accepts the current pointer event |
| Chromeless native resize | Available | Not exposed | Available when the compositor accepts the current pointer event |
| Content extended into the title bar | Available | Available | Available where GTK draws client-side decorations |
| Native window controls over extended content | Available; Windows 11 snap layouts included | Available; system-defined height and position | Not exposed; the application draws its controls |
| CSS drag regions | Native `app-region` | Emulated from the pointer press | Emulated from the pointer press |
| Built-in browser shortcuts (find, print, reload, zoom) | Can be turned off | Not present in the engine | Not present in the engine |
| Default context menu | Can be turned off | Can be turned off | Can be turned off |
| DevTools from `F12` or `OpenDevTools()` | Available | Not exposed; use the context menu or Safari | Available |
| Mutable per-window task-switcher membership | Available | Not exposed; Dock membership is application-scoped | Available as a window-manager hint |
| Separate browser data for each `UserDataRoot` | Available; the root is the WebView2 user-data folder | Available from macOS 14; WebKit keeps the data in its own container | Not mapped; persistent environments share the default session |

WebKit callback contracts sometimes require popup and dialog decisions synchronously. On macOS and
Linux, a handler that does not complete inline receives the documented safe default instead of an
unbounded asynchronous deferral.

Chromeless drag and resize entry points must be called while a native pointer press is still held.
They deliberately do not synthesize global input. A backend reports `InvalidOperationException`
when no suitable press is active and `NotSupportedException` when the operation has no safe native
implementation. Title-bar styles, drag regions, and the built-in browser feature switches are described in
[chromeless windows](chromeless-windows.md); they have been exercised on Windows 11, while the macOS
and Linux implementations have not yet been run on their target hosts. On Windows, blended caption
buttons and top-edge resizing over the browser view require an application manifest that declares
Windows 8 or later support. Linux window positioning is compositor-controlled on Wayland, so `Position`,
`PositionChanged`, startup coordinates, and position restoration are best-effort there; size, focus,
and native window-state transitions remain available.

`Position` is the top-left corner of the window, including its frame, measured from the top-left
corner of the primary display, and `ClientSize` excludes the frame and a standard title bar. Windows
and macOS report and accept both in that form, which is also the coordinate space of display
snapshots. On macOS, AppKit keeps a titled window's title bar below the menu bar, so a requested
position can be adjusted; the adjusted value is what `Position` then reports. AppKit also applies
`MinimumClientSize` and `MaximumClientSize` to interactive resizing only, so an assigned `ClientSize`
outside those limits is not clamped there as it is on Windows. The macOS conversion between this
contract and AppKit's bottom-left-origin frames is checked by the native ABI test configured for the
macOS workflow, but it has not yet been run on a macOS host.

## Browser data and user-data roots

`NeoEnvironmentOptions.UserDataRoot` names where an environment keeps cookies, local storage, IndexedDB,
and other website data. Environments on different roots must not see each other's data, and an
environment on the same root must find its data again. The backends do not all honor that yet:

- **Windows.** The root is the WebView2 user-data folder. The data is in that directory and goes away
  with it. Without a root, WebView2 uses its default folder.
- **macOS.** WKWebView does not let an application choose a storage directory. From macOS 14, each
  root selects a persistent WebKit store of its own by an identifier derived from the resolved absolute
  path of the root: symbolic links and letter case are resolved for the part of the path that exists,
  and the directory is neither created nor read. WebKit keeps these stores in its own per-application
  container, `~/Library/WebKit/<bundle identifier or executable name>/WebsiteDataStore/`, not under
  the root. Deleting, moving, or copying the root directory therefore does not delete, move, or copy
  the data: a root at the same path finds its data again, a root at a new path starts empty, and a
  root that is no longer used leaves its store behind. Clear a store with `NeoProfile.ClearDataAsync`
  on a profile of its environment, and give throwaway instances such as tests a private environment,
  which stores nothing. Without a root, the environment uses the application's default WebKit store.
  Before macOS 14 the root has no effect: every environment that is not private shares the default
  store, and the backend logs a warning when such an environment asks for a root.
- **Linux.** The root is not mapped yet: every environment that is not private shares the default
  WebKitGTK session.

A profile that is not ephemeral is the store of its environment on macOS and Linux, so it follows the
root and the private mode of that environment. Named profiles have storage of their own on Windows only.

NeoAstra releases that ignored the root on macOS kept the data of every environment in the
application's default store. That data stays there and is not copied into the store of a root: WebKit
has no public way to copy a complete store, and nothing records which root the shared data belonged
to. After an upgrade, an environment with a root therefore starts with empty storage on macOS, while an
environment without a root still opens the earlier data. An application that wants one of its
instances to keep that data leaves `UserDataRoot` unset for that instance on macOS and passes a root
for the others.

## Custom schemes and the managed bridge

- Managed resource-provider callbacks are currently synchronous on every backend. Generated byte
  responses and synchronously buffered request bodies have a 64 MiB limit; file responses avoid
  copying the entire file into managed memory.
- WebKitGTK 6.0 does not provide trustworthy initiating-origin, frame, or resource-kind metadata for
  custom-scheme requests, and its script-message callback does not provide trustworthy sender-origin
  data. Linux therefore rejects `NeoBridgePolicy.TrustedOrigins`; `TrustEntireView` reports a null
  source origin and trusts every script able to reach the handler.
- Linux custom schemes support secure and CORS flags but not authority, per-origin CORS, or service
  workers. WKWebView also does not support service workers for application-defined schemes.
- `TrustEntireView` is unsafe for content that can navigate remotely or load uncontrolled frames,
  scripts, or mutable assets. Keep bridge access default-denied or use exact trusted origins where
  the backend supports them. See the [security review](security-review.md).

## Runtime and lifecycle constraints

- NeoAstra does not bundle WebView2, WKWebView, WebKitGTK, GTK, or a .NET runtime. Missing or
  incompatible platform libraries can prevent the native library or browser backend from loading;
  install the dependencies described in [platform support](platform-support.md).
- Application and browser operations begin on the platform UI thread. An attached host must continue
  pumping that thread through asynchronous disposal; NeoAstra will not tear down COM, Cocoa, or GTK
  objects on the wrong thread after the host loop has stopped.
- Generic Host's dedicated UI-thread path has focused Windows test coverage, not macOS/AppKit
  main-thread or Linux qualification. Use explicit native-main-loop ownership where that hosted path
  has not been qualified. See [lifecycle and hosting](application-lifecycle-and-hosting.md).
- RPC channel cancellation is cooperative. A noncooperative iterator retains its service and can
  keep teardown pending after its warning; a blocked transport callback can also delay shutdown.
  Frontend channel acknowledgements mean buffer admission, not application consumption. Bounded
  overflow requires application resynchronization; it is not a durable event log.
- Linux requires a working X11 or Wayland display. A headless build, native unit test, or NativeAOT
  publish does not prove that a WebKitGTK view can be created.
- Linux tray items use the Freedesktop StatusNotifierItem protocol. KDE Plasma provides a watcher;
  GNOME generally requires an AppIndicator-compatible shell extension. NeoAstra can export and later
  register the item, but cannot make a shell display tray UI when no watcher is installed.
- Browser-engine versions and policies are controlled by the operating system or installed runtime,
  so optional behavior can differ between otherwise supported machines. Capability checks remain
  required.

## Portable behavior is intentionally bounded

NeoAstra does not provide portable arbitrary HTTP/HTTPS subresource replacement, Chrome DevTools
Protocol support, offscreen rendering, mobile support, a DOM API, release-qualified automatic updates, or a general
native widget framework. Deterministic portable bundles and inspectable installer inputs exist, but installers and the
experimental updater remain unavailable as qualified release claims until artifact-specific target-host CI passes.
Native menus, tray icons, and notifications are optional desktop-plugin services rather than core browser features.

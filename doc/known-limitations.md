# Known limitations

NeoAstra shows the browser engine of each platform rather than an engine of its own, so some behavior
differs between Windows, macOS, and Linux. This page lists those differences and the other limits to
know about. [Platforms and runtime dependencies](platform-support.md) lists the supported targets,
and [open follow-ups](open-follow-ups.md) lists the defects and checks that are still to do.

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
| Back/forward availability (`CanGoBack`, `CanGoForward`) | Available | Available | Available |
| Chromeless native drag | Available | Available | Available when the compositor accepts the current pointer event |
| Chromeless native resize | Available | Not exposed | Available when the compositor accepts the current pointer event |
| Content extended into the title bar | Available | Available | Available where GTK draws client-side decorations |
| Native window controls over extended content | Available; Windows 11 snap layouts included | Available; system-defined height and position | Not exposed; the application draws its controls |
| CSS drag regions | Native `app-region` | Emulated from the pointer press | Emulated from the pointer press |
| Built-in browser shortcuts (find, print, reload, zoom) | Can be turned off | Not present in the engine | Not present in the engine |
| Default context menu | Can be turned off | Can be turned off | Can be turned off |
| DevTools from `F12` or `OpenDevTools()` | Available | Not exposed; use the context menu or Safari | Available |
| Capture of the viewport or a region of it (`CaptureAsync`) | Available; the view must be visible | Available | Available |
| Capture of the whole document (`NeoCaptureOptions.FullPage`) | Available; each side is limited to 16,384 CSS pixels | Not exposed | Available |
| Reload that leaves the cache out (`Reload(true)`) | Available through the DevTools protocol | Available | Available |
| Browser automation (`NeoAutomation`) | Available | Available | Available |
| Messages of a page that wait for the host | No limit met with 100,000 | The page ends at 50,000 | No limit met with 100,000 |
| Mutable per-window task-switcher membership | Available | Not exposed; Dock membership is application-scoped | Available as a window-manager hint |
| Separate browser data for each `UserDataRoot` | Available; the root is the WebView2 user-data folder | Available from macOS 14; WebKit keeps the data in its own container | Available; the root holds the WebKitGTK data and cache directories |
| Private environment (`IsPrivate`) | Available; every view is InPrivate, and the in-memory data is shared across the user-data folder | Available; one in-memory store for each environment | Available; one in-memory session for each environment |

WebKit callback contracts require some decisions synchronously: a popup request on macOS and Linux,
and a file-chooser request on Linux. A handler for one of those that does not complete inline receives
the documented safe default instead of an unbounded asynchronous deferral. A JavaScript dialog waits
for its handler on every backend, for at most `NeoAstraOptions.DecisionTimeout`.

The engine's own JavaScript dialogs and the `ScriptDialogRequested` handler exclude each other; see
[built-in browser shortcuts and menus](chromeless-windows.md#built-in-browser-shortcuts-and-menus).
WebView2 raises the request only while its own dialogs are off, and reads that switch when it loads a
document: a handler assigned after a document has loaded gets the dialogs of the next one. This was
observed on Windows 11 with the WebView2 Runtime 154. A dialog that stays open until its decision
arrives was also run on macOS 15 and with WebKitGTK 2.52, in windows that are not shown, by the dialog
scenario of the conformance harness.

`NavigationCompleted` relays the browser engine's own completion notification, which does not say how
far the page's scripts have got. WKWebView can raise it while `document.readyState` is still
`interactive`, before a `<script type="module">` in the document has run and before `DOMContentLoaded`
and `load`. Classic scripts, including `defer` scripts, had already run in the same checks. Do not read
state set by a page script straight from a `NavigationCompleted` handler; wait for a signal from the
page, such as the frontend transport handshake, or until `document.readyState` is `complete`. This was
observed on macOS 26 for custom-scheme and HTTP documents. WebKitGTK uses the same engine but has not
been checked.

Do not expect `EvaluateScriptAsync` to wait for a Promise. It returns the completion value of the
script as the browser engine reports it, so a script that ends in a Promise, which every call to an
`async` function does, gives no usable result. WebView2 returns the serialized Promise object.
WKWebView and WebKitGTK fail the evaluation with a `NeoAstraException`: `Domain` `wkwebview` and
`NativeCode` 5 on macOS, `Domain` `webkitgtk` and `NativeCode` 601 on Linux. WKWebView reports that
code for any value it cannot return, such as a function or a DOM node. A script that throws fails
with the code 4 on macOS and 699 on Linux, and completes with `null` on Windows. A value without a
JSON form, such as a date or a number that is not finite, comes back as `null` on macOS. To read the
outcome of asynchronous work, have the page store it or post it as a message, then read it from a
later evaluation or the message handler.

On macOS a page must not post messages much faster than the host handles them. WebKit ends the web
content process of a page once 50,000 of its messages wait for the main thread of the application, and
drops them. The view raises `ProcessFailed` with the kind `WebProcessExited` and the recovery action
`RecreateView`. Evaluations in that view then fail with the `NativeCode` 5 described above, although
no result is at fault: check `ProcessFailed` before reading that code as a result WKWebView cannot
return. On a macOS 15 machine, one loop of 60,000 `send` calls of `@neoastra/client` ended the page,
and one of 45,000 arrived. The limit was not met with WebView2 or WebKitGTK. A page that has that many
messages should send them in groups and wait for an answer of the host between two groups, for example
the result of a call. A timer between the groups is not enough: it does not tie the page to the host,
and the engines slow the timers of a page that is not visible.

`NeoAstra.CaptureAsync` returns a PNG or JPEG image of what a view shows. The image is in device
pixels: its size is the captured size in CSS pixels multiplied by the zoom and the device scale. A
region is given in CSS pixels from the top-left corner of the visible viewport, as
`getBoundingClientRect()` reports them, and the part of it outside the viewport is left out. WebView2
captures through its DevTools protocol, which draws a frame only for a visible view, so a view in a
hidden window fails with `InvalidOperationException` there; pass a cancellation token that bounds the
wait on the other backends. WKWebView has no call for the part of a document outside the viewport, so
`FullPage` reports `NotSupportedException` on macOS; query `NeoCapability.CaptureFullPage`. On macOS a
region is scaled by the magnification only, which is exact while the view is not panned. The capture of
the viewport and of a region was run on Windows 11, on macOS 15, and with WebKitGTK 2.52, and the
capture of a whole document on Windows 11 and with WebKitGTK 2.52, by the screenshot scenario of the
conformance harness.

`NeoAutomation` works inside the page with standard DOM APIs, because the three engines share no
debugging protocol. Its input is dispatched as untrusted DOM events, its console and network lists hold
what a page can observe, and its snapshots are computed from the DOM rather than read from the
accessibility tree of the engine; [browser automation](browser-automation.md#how-it-differs-from-chrome-devtools-mcp)
lists what follows from that. The tools of Chrome DevTools MCP that depend on Chrome itself, such as
emulation, performance traces, and heap snapshots, are not provided.

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
contract and AppKit's bottom-left-origin frames is checked by the native ABI test, which passes on the
macOS runners of the native workflow.

## Browser data and user-data roots

`NeoEnvironmentOptions.UserDataRoot` names where an environment keeps cookies, local storage, IndexedDB,
and other website data. Environments on different roots do not see each other's data, and an
environment on the same root finds its data again. Where the data is kept depends on the browser engine:

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
- **Linux.** Each root has a persistent WebKitGTK network session of its own, which keeps website data
  in `data` and caches in `cache` under the root, and cookies in `data/cookies.sqlite`. The backend
  creates the two directories, accessible to the user only, and fails the creation of the environment
  when it cannot. The data goes away with the root directory. Environments of one process on the same
  root share one session, however the path is spelled. Without a root, the environment uses the default
  WebKitGTK session. WebKitGTK keeps that session in the user's data and cache directories under the
  program name of the process. A NeoAstra host normally has no program name, and WebKitGTK then uses
  `webkitgtk`, a directory that every such application shares. Cookies of the default session are in
  memory only and do not survive a restart.

A profile that is not ephemeral is the store of its environment on macOS and Linux, so it follows the
root and the private mode of that environment. Named profiles have storage of their own on Windows only.
A profile follows the private mode of its environment there as well; see
[private environments](#private-environments).

NeoAstra releases that ignored the root on macOS and Linux kept the data of every environment in the
default store: the application's default WebKit store on macOS, the default WebKitGTK session on Linux.
That data stays there and is not copied into the store of a root: WebKit has no public way to copy a
complete store on macOS, and nothing records which root the shared data belonged to. After an upgrade,
an environment with a root therefore starts with empty storage on macOS and Linux, while an environment
without a root still opens the earlier data. An application that wants one of its instances to keep
that data leaves `UserDataRoot` unset for that instance on those platforms and passes a root for the
others.

### Private environments

`NeoEnvironmentOptions.IsPrivate` asks for an environment that keeps cookies, local storage, IndexedDB,
caches, and other website data in memory only. Every view of a private environment is private, with or
without a profile. A profile that is not ephemeral does not opt out: in a private environment its data
is in memory as well. `NeoProfile.IsEphemeral` still reports what the profile was created with.

- **Windows.** WebView2 has no private environment: InPrivate mode is chosen for each view, so NeoAstra
  creates every view of a private environment as an InPrivate view. WebView2 keeps the InPrivate data of
  a profile in memory, apart from the persistent data of the same profile. Views without a profile and
  profiles without a name use the default profile and so share one in-memory store; a named profile has
  an in-memory store of its own. Three things differ from macOS and Linux:
  - The user-data folder is still used. WebView2 creates it, keeps its own browser files in it, and
    creates a directory for each profile, named or default, but an InPrivate view stores no website data
    there.
  - An in-memory store belongs to the browser process of the user-data folder, not to one environment
    or profile object, and everything on that root that selects it shares it. Private environments share
    the store of the default profile with each other and with ephemeral profiles that have no name;
    ephemeral profiles with the same name share theirs. Give a profile a name of its own to keep its
    data apart.
  - WebView2 discards the InPrivate data of a profile when the last view using it closes, which can be
    before the environment is disposed.

  A WebView2 Runtime without profile support, which came with version 101, cannot create InPrivate
  views: view creation fails there rather than store the data of a private environment. The Windows
  mapping has not yet been run on its target host.
- **macOS.** A private environment has one non-persistent WebKit store, which lasts as long as the
  environment. A profile that is not ephemeral uses that store, and an ephemeral profile has a
  non-persistent store of its own.
- **Linux.** A private environment has one ephemeral WebKitGTK network session, which lasts as long as
  the environment. A profile that is not ephemeral uses that session, and an ephemeral profile has an
  ephemeral session of its own.

NeoAstra releases that ignored `IsPrivate` on Windows created a view of a private environment on a
persistent profile of the user-data folder unless the view had an ephemeral profile. Website data
written by those releases is still in that folder, and a private environment neither reads nor removes
it. Delete the folder while nothing uses it, or clear it with `NeoProfile.ClearDataAsync` on a profile
of an environment on the same root that is not private.

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

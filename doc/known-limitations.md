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

## Open follow-ups

Known or suspected defects that are not fixed yet, and checks that have not been run. Remove an entry
when it is done.

- **Linux: a download may be reported once for each environment that shares its session.** The GTK
  backend connects a download handler to the network session of every environment it creates
  (`neo_platform_environment_create_async` in `native/src/linux/gtk_backend.cpp`). Environments of one
  process share a session when they are on the same `UserDataRoot` or have none, so a download from a
  view of that session is expected to reach `download_started` once for each of them, and
  `DownloadRequested` and the download events to be raised more than once for it. This follows from
  the source and has not been reproduced. To do: reproduce it in `native/tests/linux_backend_tests.cpp`
  with two environments on one root and one download, then connect the handler once for each session,
  still canceling the downloads of views that NeoAstra does not own only while an environment uses
  that session. Until then, create one environment for each root in a process.
- **Linux: applications without a root share one default session.** A NeoAstra host has no GLib
  program name, so WebKitGTK keeps the default session of every such application in the same
  `webkitgtk` directories and keeps its cookies in memory; see
  [browser data and user-data roots](#browser-data-and-user-data-roots). To decide: whether a host
  gets default directories of its own, for example from `NeoApplicationOptions.ApplicationName`, and
  what happens to the data already in the shared ones.
- **Linux: the user-data root and view teardown fixes were run on arm64 only.** They were built and
  tested in an Ubuntu 24.04 arm64 virtual machine with WebKitGTK 2.52: the native tests, the sanitizer
  and static-analyzer presets, and the release build. Not run: linux-x64, and any check that loads a
  page with WebKit's sandbox enabled. In that virtual machine, reached over SSH and under Xvfb, loading
  a page aborted with `Failed to fully launch dbus-proxy`, with or without `dbus-run-session`, so the
  end-to-end check of local storage and cookies across processes ran with
  `WEBKIT_DISABLE_SANDBOX_THIS_IS_DANGEROUS=1`. The cause was not established, nor whether an
  application started from a desktop session is affected. To do: on a Linux x64 desktop host, run
  `xvfb-run -a python eng/build_native.py --rid linux-x64 --clean` and a check that loads a page, such
  as the browser conformance harness.
- **Linux: `CanGoBack` and `CanGoForward` are never reported.** The managed view updates the two
  properties only from the native history-changed event (`NEOASTRA_EVENT_HISTORY_CHANGED`, handled in
  `src/NeoAstra.Core/NeoAstra.cs`), and the GTK backend never raises it:
  `native/src/linux/gtk_backend.cpp` connects nothing to the back/forward list of a view. Both
  properties are therefore expected to stay `false` on Linux while `GoBack` and `GoForward` still
  navigate, and the browser conformance scenario "navigation, history, and redirects", which waits for
  `CanGoBack`, is expected to time out. macOS had the same defect until the Cocoa backend began
  observing `canGoBack` and `canGoForward`. The Linux one follows from the source and has not been
  run. To do: when a view is created, connect the `changed` signal of the list returned by
  `webkit_web_view_get_back_forward_list` and raise the event with bit 0 from
  `webkit_web_view_can_go_back` and bit 1 from `webkit_web_view_can_go_forward`, as `report_history`
  does in `native/src/macos/cocoa_backend.mm`. Cover it in `native/tests/linux_backend_tests.cpp` the
  way `native/tests/macos_history_tests.mm` does on macOS, which needs a host where pages load (see
  the entry above), then update the back/forward row of the table in the next section.
- **macOS: the history test has not run on the CI runners.** `native/tests/macos_history_tests.mm` is
  the first native test that loads pages, so WebKit's web content process must be able to start where
  `ctest` runs. It passed on macOS 26.5 for arm64, for x86_64 under Rosetta, and with the sanitizer
  preset built for arm64. It has not run on macOS 15, on the `macos-15-intel` and `macos-latest`
  runners of the native workflow, or in the x64 sanitizer job. To do: check
  `neoastra_macos_history_tests` in the next native workflow run. Where pages cannot load, it fails at
  its first page and prints the page and the history flags it saw last.
- **macOS: `NavigationCompleted` can arrive before the module scripts of a page have run.** The Cocoa
  backend relays WKWebView's own notification; see the paragraph on `NavigationCompleted` under
  [backend capability differences](#backend-capability-differences). To decide: whether the backend
  should hold the event until the document has loaded, so that it means the same on every backend, or
  keep relaying the engine. WebKitGTK uses the same engine and has not been checked.
- **Conformance: not rerun on Windows or Linux.** Two changes to
  `src/NeoAstra.Conformance/Program.cs` have run on macOS only. The first scenario and the
  100,000-message stress scenario wait until `document.readyState` is `complete` before they use what
  the fixture module sets, because of the `NavigationCompleted` timing above. The optional "Promise
  results" and "IndexedDB" scenarios are reported as skips, instead of ending the run, when the
  backend rejects a script result that is a Promise, as WKWebView does; see the paragraph on
  `EvaluateScriptAsync` under [backend capability differences](#backend-capability-differences). On
  macOS 26.5 for arm64, with the current `osx-arm64` runtime, the whole run completes with exit code
  0, in normal mode and with `--stress`. Neither Windows nor Linux was run after these changes. To do:
  run `dotnet run --project src/NeoAstra.Conformance -c Release -- --run --stress --timeout-seconds 30`
  from the repository root on each platform. On Linux the run is expected to stop at "Promise results"
  if WebKitGTK rejects a Promise result (see the next entry), and otherwise at "navigation, history,
  and redirects" until `CanGoBack` is reported there.
- **Conformance: a rejected Promise result is recognized on macOS only.** `IsUnsupportedResultType` in
  `src/NeoAstra.Conformance/Program.cs` turns one error into a skip of the "Promise results" and
  "IndexedDB" scenarios: a `NeoAstraException` with the domain `wkwebview` and the native code 5,
  which is how WKWebView refuses to return a script result. WebKitGTK has an error of its own for
  that, `WEBKIT_JAVASCRIPT_ERROR_INVALID_RESULT` (601), which its own tests expect for a DOM node, and
  the GTK backend passes the code of an evaluation error on under the domain `webkitgtk`
  (`script_finished` in `native/src/linux/gtk_backend.cpp`). If WebKitGTK reports a Promise that way,
  "Promise results" ends the run on Linux with that exception, as it did on macOS. This follows from
  the WebKitGTK source and has not been run. To do: run the harness on Linux and, if the scenario
  fails with the native code 601, add that domain and code to `IsUnsupportedResultType`. A script that
  throws is `WEBKIT_JAVASCRIPT_ERROR_SCRIPT_FAILED` (699) there and must keep failing its scenario.
- **Conformance: "IndexedDB" is skipped on every backend.** The scenario evaluates an `async` function
  and reads the value that `EvaluateScriptAsync` returns, so it can pass only where a Promise result
  is awaited, and no backend is known to do that; see the next entry. IndexedDB itself is therefore
  not checked anywhere. It does work on WKWebView in the harness configuration, which is an
  application-defined scheme, a private environment, an ephemeral profile, and the content security
  policy of the fixture: with handlers appended that store the value or the error in a global, and
  the script ending in `true`, a later evaluation read `{"ok":"stored"}`. That was observed on macOS
  26.5 for arm64 with a throwaway probe, not with the harness. To do: rewrite the scenario that way,
  waiting for the stored outcome with `WaitUntilScriptAsync`, so that it no longer goes through
  `RunPromiseCaseAsync`. The fixture's `script-src 'self'` refuses `eval` and `new Function`, so the
  probe has to stay plain script. To decide: whether it then becomes a required case, which fails the
  run on a backend where IndexedDB does not work for an application-defined scheme. The result of the
  scenario changes on every platform, so run each.
- **`EvaluateScriptAsync` cannot return the result of a Promise.** See the paragraph on
  `EvaluateScriptAsync` under [backend capability differences](#backend-capability-differences):
  WKWebView rejects such a result and WebView2 serializes the Promise object. The engines await a
  Promise through a separate call, which takes the body of a function, with `return`, rather than a
  script whose completion value is the result:
  `callAsyncJavaScript:arguments:inFrame:inContentWorld:completionHandler:` on WKWebView from macOS
  11, and `webkit_web_view_call_async_javascript_function` on WebKitGTK from 2.40. For WebView2 the
  DevTools protocol method `Runtime.evaluate` with `awaitPromise`, reached through
  `CallDevToolsProtocolMethod`, is a candidate. None of the three has been tried. Running an existing
  script through them with `eval` is not an option, because a page whose content security policy
  lacks `'unsafe-eval'` refuses it: under the `script-src 'self'` of the conformance fixture, `eval`,
  indirect `eval`, and `new Function` all failed with an `EvalError` on WKWebView. To decide: whether
  NeoAstra exposes that call as an operation of its own, with a function body and arguments, in the
  native ABI and the managed API, or leaves asynchronous results to messaging. It needs a native
  rebuild of every runtime, and on macOS it depends on the minimum supported version, which is not
  frozen. Until then the conformance scenario "Promise results" cannot pass.
- **Most checked-in native runtimes predate the latest fixes.** Only `osx-arm64` was rebuilt, for the
  macOS user-data root and history fixes. The other five runtimes under `src/NeoAstra.Core/runtimes`
  predate the fixes for their platform (the user-data root fixes for macOS and Linux, the macOS
  history fix, the private-environment fix for Windows, and the Linux view teardown fix) until they
  are replaced with the artifacts of a native workflow run. A macOS build takes its minimum system
  version from the machine that builds it, so replacing `osx-x64`, which the workflow builds for
  macOS 15, with a build from a newer macOS would raise it.

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
| Back/forward availability (`CanGoBack`, `CanGoForward`) | Available | Available | Not exposed; both stay `false` |
| Chromeless native drag | Available | Available | Available when the compositor accepts the current pointer event |
| Chromeless native resize | Available | Not exposed | Available when the compositor accepts the current pointer event |
| Content extended into the title bar | Available | Available | Available where GTK draws client-side decorations |
| Native window controls over extended content | Available; Windows 11 snap layouts included | Available; system-defined height and position | Not exposed; the application draws its controls |
| CSS drag regions | Native `app-region` | Emulated from the pointer press | Emulated from the pointer press |
| Built-in browser shortcuts (find, print, reload, zoom) | Can be turned off | Not present in the engine | Not present in the engine |
| Default context menu | Can be turned off | Can be turned off | Can be turned off |
| DevTools from `F12` or `OpenDevTools()` | Available | Not exposed; use the context menu or Safari | Available |
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
observed on Windows 11 with the WebView2 Runtime 154. The WebKitGTK path, which keeps a dialog open
until its decision arrives, was written from the WebKitGTK documentation and has not been run.

`NavigationCompleted` relays the browser engine's own completion notification, which does not say how
far the page's scripts have got. WKWebView can raise it while `document.readyState` is still
`interactive`, before a `<script type="module">` in the document has run and before `DOMContentLoaded`
and `load`. Classic scripts, including `defer` scripts, had already run in the same checks. Do not read
state set by a page script straight from a `NavigationCompleted` handler; wait for a signal from the
page, such as the frontend transport handshake, or until `document.readyState` is `complete`. This was
observed on macOS 26 for custom-scheme and HTTP documents. WebKitGTK uses the same engine but has not
been checked.

Do not expect `EvaluateScriptAsync` to wait for a Promise. It returns the script's completion value as
the browser engine reports it, so a script that ends in a Promise, which every call to an `async`
function does, gives no usable result. WKWebView fails such an evaluation at once with a
`NeoAstraException` whose `Domain` is `wkwebview` and whose `NativeCode` is 5, the error it reports for
any value it cannot return, a function or a DOM node included. A script that throws arrives as the same
exception type with `NativeCode` 4. WebView2 completes the evaluation with the serialized Promise object
instead. To read the outcome of asynchronous work, have the page store it or post it as a message, then
read it from a later evaluation or the message handler. The WKWebView behavior was observed on macOS 26.
The WebView2 behavior is the one the conformance harness was written against and was not re-checked
for this note, and WebKitGTK has not been checked.

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

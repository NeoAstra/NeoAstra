# Open follow-ups

Notes for the people who work on NeoAstra: known or suspected defects that are not fixed yet, and
checks that have not been run. Remove an entry when it is done. What an application has to know is in
[known limitations](known-limitations.md).

- **Windows ARM64 has not been run.** The `win-arm64` library is cross-built and packaged, and its
  native tests are skipped. No browser view was created on ARM64 hardware.
- **Windows: a window is counted in the pixels of its display, and `ScaleFactor` is 1 until the scale
  changes.** `NeoWindow.ClientSize`, `Position`, and the size limits are documented as logical units,
  which is also what the specification asks of a backend. The Windows backend reports and takes what
  Win32 does (`sync_bounds` and `neo_platform_window_set_bounds` in
  `native/src/windows/windows_backend.cpp`): pixels of the display in a process whose manifest declares
  per-monitor awareness, and scaled units in a process that declares none. It raises the scale-factor
  event from `WM_DPICHANGED` only, so a window created on a display that scales keeps a `ScaleFactor`
  of 1 until the scale changes; the macOS and GTK backends never raise the event. Seen on Windows 11 at
  150 percent in a process with per-monitor awareness: a window with a `ClientSize` of 900 by 700 and
  a `ScaleFactor` of 1 showed a page of 600 by 467 CSS pixels with a device pixel ratio of 1.5. This
  is why `resize_page` of [browser automation](browser-automation.md) reads the scale from the page.
  `NeoWindowStateController` saves `ScaleFactor` with a placement, and `NeoWindowStateRestore.Clamp`
  scales the saved size by its ratio to the scale of the display, so a placement saved on such a display
  is expected to come back at two thirds of its size. That follows from the source and has not been
  reproduced. To decide: whether the backend converts to logical units or the documentation says
  pixels. To do: report the scale of a window when it is created. Both change the Windows runtimes.
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
  [browser data and user-data roots](known-limitations.md#browser-data-and-user-data-roots). To decide: whether a host
  gets default directories of its own, for example from `NeoApplicationOptions.ApplicationName`, and
  what happens to the data already in the shared ones.
- **Linux: a page does not load on the runner of the conformance workflow.** WebKitGTK starts its web
  process in a bubblewrap sandbox, which needs unprivileged user namespaces, and Ubuntu 24.04
  restricts those through AppArmor. On the Linux runner of the workflow, Ubuntu 24.04 under Xvfb, the
  desktop smoke fixture passes and the browser conformance harness then aborts at its first page with
  `bwrap: loopback: Failed RTM_NEWADDR: Operation not permitted`. The harness completes on Ubuntu 24.04
  x64 in a WSL 2 desktop session with WebKitGTK 2.52 and the sandbox left as it is. The workflow now
  lifts the restriction on its runner, with
  `sudo sysctl -w kernel.apparmor_restrict_unprivileged_userns=0` before the harness. To do: run the
  workflow and see that the harness completes there. An Ubuntu 24.04 arm64 virtual machine, reached over SSH and under Xvfb, aborted with
  `Failed to fully launch dbus-proxy` when it loaded a page, with or without `dbus-run-session`, and
  the checks there ran with `WEBKIT_DISABLE_SANDBOX_THIS_IS_DANGEROUS=1`; whether that had the same
  cause is not known.
- **Linux: `ClientSize` is the size a window was asked for, not the one it has.** The GTK backend
  keeps the size it was last given and hands it to `gtk_window_set_default_size`
  (`neo_platform_window_set_bounds` in `native/src/linux/gtk_backend.cpp`). It means to follow the
  window through `notify::width` and `notify::height`, which a `GtkWindow` of GTK 4 does not have, so
  `window_size_changed` is not expected to run. Seen with WebKitGTK 2.52 in a WSL 2 desktop session: a
  window with a `ClientSize` of 1000 by 737 still reported that size when it was maximized and its page
  was 3840 by 2051 CSS pixels, and a window given 6000 by 4037 reported that size while its page stayed
  2123 CSS pixels high, the height of the screen less the title bar. `resize_page` therefore goes by
  the sizes it gave a window and by what the page shows. Whether a window that the user resizes is
  followed, and whether `BoundsChanged` is raised for it, was not checked. To do: follow
  `notify::default-width` and `notify::default-height`, and a maximized or fullscreen window through
  the size of its surface. That changes the Linux runtimes.
- **Linux: a command at the top level of a menu bar is dropped.** A GTK 4 menu bar
  (`GtkPopoverMenuBar`) shows the submenus of its model and nothing else. The menu presenter
  (`src/NeoAstra/Desktop/LinuxMenus.cs`) hands a command or a role item at the top level of an
  application or window menu on to GTK, which logs "Don't know how to handle this item" and leaves it
  out. The desktop smoke fixture sets such menus. To decide: whether the presenter refuses such an
  item, as it refuses a role item without a label, or puts it into a submenu.
- **Linux: the history flags have no native test.** The GTK backend raises the history-changed event
  (`NEOASTRA_EVENT_HISTORY_CHANGED`) from the `changed` signal of the back/forward list of a view, and
  again as a document commits and before its load is reported as finished, with bit 0 from
  `webkit_web_view_can_go_back` and bit 1 from `webkit_web_view_can_go_forward`. `CanGoBack` and
  `CanGoForward` stayed `false` on Linux before that, and the browser conformance scenario "navigation,
  history, and redirects" timed out there, as it was seen to do with WebKitGTK 2.52. With the runtime
  built from `15ab7e2` the scenario passes. It is the only check of the flags on Linux. To do: cover
  them in `native/tests/linux_backend_tests.cpp` the way `native/tests/macos_history_tests.mm` does on
  macOS, which needs a host where pages load.
- **Automation: the size of a page was checked on one display scale, and not on macOS.** `resize_page`
  takes a CSS pixel of a page for as many units of its window as the zoom of the view says on macOS
  and Linux: the `magnification` of a WKWebView, the zoom level of a WebKitGTK view. The conformance
  scenario "screenshots, coordinates, and resizing in a shown window" checks it with a page of 400 by
  300 CSS pixels in a view zoomed to 200 percent. With that scenario, and with the one for an element
  with an EditContext, the harness run with `--run --stress --timeout-seconds 30` completes on Windows
  11 x64 with WebView2 (30 passed, 19 skipped) and on Ubuntu 24.04 x64 with WebKitGTK 2.52 in a WSL 2
  desktop session (30 passed, 19 skipped; the EditContext scenario is one of the skipped, because
  WebKit has no EditContext). It was not run on macOS. That `innerWidth` of a magnified WKWebView
  shrinks by the magnification follows from the WebKit source, where
  `LocalFrameView::mapFromLayoutToCSSUnits` divides the size of the view by the page zoom and by the
  scale of the frame. Where it does not, the size that the page shows after the first try still leads
  `resize_page` to the next one. On Windows the harness is not aware of the scale of its display, so
  the unit test `ResizePageGivesThePageItsSizeWhereAWindowIsCountedInThePixelsOfItsDisplay` is what
  runs a window counted in pixels. It ran on a display at 150 percent. The scales of 100, 125, 175,
  200, and 250 percent were forced on WebView2 with `--force-device-scale-factor` in a throwaway host,
  and each page size from 300 to 420 CSS pixels asked for: all of them were shown, but for the sizes
  of 4n + 1 CSS pixels at 125 percent, which no window size gave. To do: run the conformance workflow,
  and the unit test on a display at another scale.
- **macOS: `NavigationCompleted` can arrive before the module scripts of a page have run.** The Cocoa
  backend relays WKWebView's own notification; see the paragraph on `NavigationCompleted` under
  [backend capability differences](known-limitations.md#backend-capability-differences). To decide: whether the backend
  should hold the event until the document has loaded, so that it means the same on every backend, or
  keep relaying the engine. WebKitGTK uses the same engine and has not been checked.
- **macOS: nothing keeps a page from posting more messages than WKWebView lets wait.** WKWebView ends
  the web content process of a page that has 50,000 messages waiting in the application process; see
  the paragraph on message bursts under
  [backend capability differences](known-limitations.md#backend-capability-differences). That is what failed the
  conformance scenario "100,000 small messages" on the `macos-15-intel` runner of the conformance
  workflow, with the error of an unsupported script result, where its single loop of posts had passed
  on macOS 26.5 for arm64. The scenario now posts bursts of 10,000 and waits for each one to arrive.
  With that the harness, run with `--run --stress --timeout-seconds 30`, completes on that runner (30
  passed, 19 skipped), on Windows 11 x64 with WebView2, on the runner of the workflow and on a
  development machine (29 passed, 19 skipped), and on Ubuntu 24.04 x64 with WebKitGTK 2.52 in a WSL 2
  desktop session (30 passed, 18 skipped). On the macOS runner the 100,000 messages took 13 to 20 s to
  arrive, of the 30 s that the workflow gives a scenario. The frontend transport does no such thing:
  `send` of `@neoastra/client` posts at once, so the page of an application can end the same way. To
  decide: whether the bootstrap (`src/NeoAstra.Core/Transport/transport-bootstrap.js`) holds frames
  back on WKWebView while too many are unanswered. `postMessage` on a script message handler returns a
  Promise that settles when the application process has handled the message. A throwaway sender that
  left at most 4,096 messages unanswered had no more than 4,098 waiting on the runner, and delivered
  100,000 messages there in 11.6 s and with WebKitGTK 2.52 in 1.8 s. Such a sender goes on only while
  the page runs, though, and the window of the harness is not shown: in another run on the runner the
  web content process stopped taking the replies 8 s after its view was created, with 84,336 of the
  100,000 messages delivered, and went on when the harness evaluated a script 25 s later. Whether a
  page in a window that is shown stops that way was not checked.

  What was measured:

  On macOS a page must not post messages much faster than the host handles them. A message that a page
  posts goes from its web content process to the application process, where WebKit queues it for the
  main thread and hands over at most 600 messages for each turn of the run loop, and as few as 60 while
  the queue stays long. Once 50,000 messages of a web content process wait in that queue, WebKit ends
  the process as misbehaving and drops them (`maxPendingIncomingMessagesKillingThreshold` in WebKit's
  `Source/WebKit/Platform/IPC/Connection.cpp`, a limit compiled for the Apple platforms only). The view
  raises `ProcessFailed` with the kind `WebProcessExited` and the recovery action `RecreateView`. An
  evaluation that was waiting for its result fails with the `NativeCode` 5 described above, and so does
  an evaluation started later in that view, although no result is at fault: check `ProcessFailed` before
  reading that code as a result WKWebView cannot return. Whether a burst gets that far depends on the
  machine. On the `macos-15-intel` runner of the conformance workflow, with macOS 15.7 and WebKit
  20621.3.11, the page of the conformance fixture posted 10,000 messages through `@neoastra/client` in
  0.1 to 0.35 s, and the harness received 5,000 to 9,000 a second. A loop of 20,000 or of 45,000 `send`
  calls arrived there. A loop of 60,000, of 80,000, or of 100,000 ended the page after 0.8 to 1 s, when
  4,500 to 8,400 messages had arrived, and each time WebKit wrote to the system log "Over 50000
  incoming messages have been queued without the main thread processing them, terminating the remote
  process as it seems to be misbehaving". The loop of 100,000 had arrived on macOS 26.5 for arm64, and
  it arrives with WebView2 on Windows 11 and with WebKitGTK 2.52. A page that has that many messages
  should send them in groups and wait for an answer of the host between two groups, for example the
  result of a call. A timer between the groups does not tie the page to the host, and the engines slow
  the timers of a page that is not visible: a page that posted 1,000 messages from each turn of a
  zero-delay timer, in a window that is not shown, got 19,000 of them to the host in 26 s on that runner,
  36,000 in 25 s with WebKitGTK, and 32,000 in 25 s with WebView2.
- **macOS: an evaluation in a view that lost its web content process reports an unsupported result.**
  WKWebView completes it with `WKErrorJavaScriptResultTypeIsUnsupported` (5), whether the evaluation
  was waiting when the process ended or was started afterwards, and the Cocoa backend relays that
  error (`neo_platform_view_evaluate` in `native/src/macos/cocoa_backend.mm`). Only `ProcessFailed`
  says what happened. To decide: whether the backend reports such an evaluation as a failure of the
  process, with an error of its own. That changes the macOS runtimes.
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
  `EvaluateScriptAsync` under [backend capability differences](known-limitations.md#backend-capability-differences):
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
- **macOS: a runtime takes its minimum system version from the machine that builds it.** The six
  runtimes under `src/NeoAstra.Core/runtimes` are the artifacts of one native workflow run. The
  workflow builds `osx-x64` on macOS 15 and `osx-arm64` on macOS 26, and the libraries record those as
  the oldest systems they load on: 15.0 for `osx-x64` and 26.0 for `osx-arm64`. To decide: the minimum
  supported macOS version, which the build then has to be given as its deployment target.


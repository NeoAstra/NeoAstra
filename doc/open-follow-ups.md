# Open follow-ups

Notes for the people who work on NeoAstra: known or suspected defects that are not fixed yet, and
checks that have not been run. Remove an entry when it is done. What an application has to know is in
[known limitations](known-limitations.md).

- **Windows ARM64 has not been run.** The `win-arm64` library is cross-built and packaged, and its
  native tests are skipped. No browser view was created on ARM64 hardware.
- **Windows: a window is counted in pixels where the application is aware of display scaling.** The
  specification asked for logical units on every platform, which is what `NeoWindow.ClientSize`,
  `Position`, the size limits, and the sizes of `NeoWindowOptions` are on macOS and with GTK. The
  Windows backend reports and takes what Win32 does (`sync_bounds`, `WM_SIZE`, `WM_GETMINMAXINFO`, and
  `neo_platform_window_set_bounds` in `native/src/windows/windows_backend.cpp`): the pixels of the
  display in a process that declares awareness, and logical units in a process that declares none. On
  2026-10-07 this was kept and documented, with `ScaleFactor` reported from the creation of a window,
  because converting changes the size of every window of an aware application. To do, in a version
  that may break those: convert in the backend, together with the bounds of a view (`view_bounds`) and
  the positions of a drop, which are in the same pixels. What goes by the pixels today:
  `NeoAutomationPage.ResizeCoreAsync`, which takes the scale from the page on Windows,
  `NeoWindowStateRestore.Clamp` with its `countsInDisplayPixels`, and the test
  `WindowBounds_RoundTripClientSizeAndPositionWhenDevelopmentLibraryIsAvailable`. To settle first:
  WebView2 gives a page the CSS pixels that cover its window, so a logical size has to become pixels
  rounded down for the page to have that size; and displays with different scales have logical
  coordinates that overlap or leave gaps, because a display snapshot divides each display by its own
  scale, so a position needs a rule for the display it is on.
- **Window sizes and scale were run at one scale.** `ScaleFactor` at the creation of a window, and a
  placement that `NeoWindowStateController` saves and restores, were run on Windows 11 at 150 percent,
  in a thread with per-monitor awareness and in one without. Not run: a window that moves between
  displays with different scales (`WM_DPICHANGED`), other scales, and a restore onto a display with
  another scale, which only the unit test
  `WindowStateKeepsTheSizeOfAWindowWhateverTheScaleOfItsDisplay` covers, with made-up displays. A
  placement that was saved before `ScaleFactor` was right carries a scale of 1: on Windows, in an
  aware application on a display that scales, such a window comes back larger by that scale once. The
  macOS backend reads `backingScaleFactor` and follows `windowDidChangeBackingProperties:`. The
  conformance scenario "a window reports the scale of its display and the size it has" passes on the
  `macos-15-intel` runner of the conformance workflow, where the scale of a window is the device pixel
  ratio of its page and a maximized window reports its size; whether that display has two pixels for a
  point is not known, and a change of scale was not run. The GTK backend was run at a scale of 1, with
  GTK 4.14 in a WSL 2 desktop session and under Xvfb on the runners, where no window manager maximizes
  a window, and a user who drags an edge is stood in for by a size given through GTK
  (`test_window_reports_the_size_it_has` in `native/tests/linux_backend_tests.cpp`).
- **Window state was run on Windows only.** `NeoWindowStateController` saves the bounds that a window
  has in its normal state, and reads the window once it was left alone for its delay. That is what
  keeps the normal bounds of a window that the user maximizes: Windows tells a window of its new
  bounds before it tells it of its new state. It was run on Windows 11 with the commands that a title
  bar sends (`WindowStateKeepsTheNormalBoundsOfAWindowThatIsNotInItsNormalState`). It was not run on
  macOS and with GTK, where a change of state takes the platform some time. A window that is read on
  the way there, because an earlier change started the delay or because the controller ends just
  then, can show the state it goes to with the bounds it comes from, and that is what is saved until
  its next change. Normal bounds that the backend reports, from the placement of Win32 and the
  default size of a GTK window, would not depend on the timing. On Windows a window that the user
  snaps to a side of its screen is in its normal state, so the snapped bounds are saved as its normal
  bounds, where the system itself keeps the ones from before.
- **Windows: a minimized window reports no size.** `Position` is -32000 by -32000 and `ClientSize` is
  0 by 0 while a window is minimized, which is what `WM_MOVE` and `WM_SIZE` say
  (`native/src/windows/windows_backend.cpp`). `NeoWindowStateController` leaves such bounds out. To
  decide: whether the backend keeps reporting the bounds that the window goes back to while it is
  minimized.
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
- **Linux arm64: a page did not load in a virtual machine.** An Ubuntu 24.04 arm64 virtual machine,
  reached over SSH and under Xvfb, aborted with `Failed to fully launch dbus-proxy` when it loaded a
  page, with or without `dbus-run-session`, and the checks there ran with
  `WEBKIT_DISABLE_SANDBOX_THIS_IS_DANGEROUS=1`. The cause is not known. It may be the one that kept
  the x64 runner of the conformance workflow, Ubuntu 24.04 under Xvfb, from loading a page: WebKitGTK
  starts its web process in a bubblewrap sandbox, which needs unprivileged user namespaces, and
  Ubuntu 24.04 restricts those through AppArmor, so the harness aborted at its first page with
  `bwrap: loopback: Failed RTM_NEWADDR: Operation not permitted`. The workflow lifts the restriction
  on its runner, with `sudo sysctl -w kernel.apparmor_restrict_unprivileged_userns=0` before the
  harness, and the harness completes there since (31 passed, 19 skipped on 2026-10-07).
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
- **Automation: the size of a page was checked on one display scale.** `resize_page` takes a CSS pixel
  of a page for as many units of its window as the zoom of the view says on macOS and Linux: the
  `magnification` of a WKWebView, the zoom level of a WebKitGTK view. The conformance scenario
  "screenshots, coordinates, and resizing in a shown window" checks it with a page of 400 by 300 CSS
  pixels in a view zoomed to 200 percent. With that scenario, the one for an element with an
  EditContext, and the one for the scale and the size of a window, the harness run with
  `--run --stress --timeout-seconds 30` completes on Windows 11 x64 with WebView2 (31 passed, 19
  skipped), on Ubuntu 24.04 x64 with WebKitGTK 2.52 in a WSL 2 desktop session (31 passed, 19 skipped;
  the EditContext scenario is one of the skipped, because WebKit has no EditContext), and on the three
  runners of the conformance workflow, where macOS ran it for the first time on 2026-10-07, on
  `macos-15-intel` with WKWebView (31 passed, 20 skipped). That `innerWidth` of a magnified WKWebView
  shrinks by the magnification follows from the WebKit source, where
  `LocalFrameView::mapFromLayoutToCSSUnits` divides the size of the view by the page zoom and by the
  scale of the frame; the scenario passes there, which does not tell whether the first try gave the
  size or a later one. On Windows the harness is not aware of the scale of its display, so
  the unit test `ResizePageGivesThePageItsSizeWhereAWindowIsCountedInThePixelsOfItsDisplay` is what
  runs a window counted in pixels. It ran on a display at 150 percent. The scales of 100, 125, 175,
  200, and 250 percent were forced on WebView2 with `--force-device-scale-factor` in a throwaway host,
  and each page size from 300 to 420 CSS pixels asked for: all of them were shown, but for the sizes
  of 4n + 1 CSS pixels at 125 percent, which no window size gave. To do: run the unit test on a
  display at another scale.
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
- **Links that open outside the view: no test opens an address that does open.** On 2026-10-08 four
  changes went in: the view setting `NEOASTRA_VIEW_SETTING_TAB_FOCUSES_LINKS`, the bits of a
  navigation or new-window request (`NEOASTRA_NAVIGATION_REQUEST_USER_INITIATED`,
  `NEOASTRA_REQUEST_LINK_ACTIVATED`), the common path of an `OpenExternal` decision (`open_external`
  and `neoastra_decision_complete` in `native/src/common/neoastra.cpp`, `neo_external_uri_allowed` in
  `native_internal.hpp`), and `NEOASTRA_EVENT_EXTERNAL_OPEN_COMPLETED`. They were built and run on
  macOS 26 arm64 (`ctest` with `neoastra_macos_link_tests`, the unit tests, the conformance harness)
  and on Windows 11 with the WebView2 Runtime 154 (`ctest`, the unit tests with
  `NavigationRequestTests`, the conformance harness, and a program outside the repository that sent
  clicks and key presses to a view through the DevTools protocol and counted the requests of a server
  on the loopback interface).
  The Linux backend was compiled and run the same day: by the native workflow, where
  `neoastra_linux_backend_tests` passes with `test_tab_key_setting_reaches_the_view`, and by the
  conformance harness with WebKitGTK 2.52 in WSL, where the scenario "an address that is not a web
  address is not sent outside the view" passes. The native workflow also ran
  `neoastra_macos_link_tests` on an Intel Mac with macOS 15.
  On Windows that program saw `Refused` for `mailto:`, `tel:`, an unregistered scheme, and an address
  with credentials, from a link and from a link with `target="_blank"`, and `Opened` with one request
  of the default browser for a link, for a link with `target="_blank"`, and for addresses of 2000 to
  32,000 characters. Still open: the repository has no test that opens an address which does open,
  because that starts the browser of the machine. On macOS the native test takes the place of
  `NSWorkspace`, on Windows it was seen by hand as said, and on Linux it was not seen. `Failed` was
  seen on no platform. To decide: whether the native library gets a way to replace the system opener
  in a test, as the macOS test does for itself.
- **`IsUserInitiated` was not observed on Linux.** The table in
  [links that open outside the web view](capabilities-and-security.md#links-that-open-outside-the-web-view)
  has its macOS column from a native test, its Windows column from the run above, and its Linux
  column from what WebKitGTK (`webkit_navigation_action_is_user_gesture`) documents. To check on
  Linux: a link that the user clicks; `location.href`, `window.open`, and `click()` on a link from a
  click handler; the same from a timer; and the redirect of a clicked link. A script that the host
  runs with `EvaluateScriptAsync` has the user gesture in WebView2, and had it in WKWebView (its
  private `_isUserInitiated` was 1, and 0 in a timer that ran three seconds later), so on Linux a
  navigation such a script starts may be user-initiated: the tests have to let the page act by
  itself, as `AScriptThatActsOnItsOwnIsNotUserInitiated` in `NavigationRequestTests` does on Windows.
  Linux reported a navigation as user-initiated by its navigation type before; it now uses the user
  gesture, which a link clicked by a script running on its own does not have.
- **Windows: a script of the host counts as an action of the user.** WebView2 runs the script of
  `ExecuteScript` with a user gesture: for about five seconds after `EvaluateScriptAsync`, whatever
  the script does, a navigation or a new window that the page asks for by itself is user-initiated
  (seen one second and four seconds after `EvaluateScriptAsync("1 + 1")`, not seven seconds after,
  and not after `PostMessageAsync`). The policy of `NeoApp` for external links requires a user
  action, so an application that evaluates scripts in its view lets its page open the system browser
  without a click on Windows; `NeoAutomation` evaluates scripts as well. A candidate, not tried: the
  DevTools protocol method `Runtime.evaluate` with `userGesture: false`, reached through
  `CallDevToolsProtocolMethod`, which the entry on Promise results above names too.
- **Windows: the navigation of a subframe is not reported.** The WebView2 backend listens to
  `NavigationStarting` of the top-level document and has no handler for the event of frames
  (`FrameNavigationStarting`): a click on a link in an `<iframe>` loaded the address in the frame and
  raised no `NavigationRequested`, so the policy of `NeoApp`, which cancels the navigation of a
  subframe that leaves the origin of the application on macOS, does not on Windows. To do: raise the
  request without `NEOASTRA_NAVIGATION_REQUEST_MAIN_FRAME` for frames, honor the decision, and answer
  the request of a refused one as `take_refused_navigation` does for the top-level document.
- **macOS: the redirect of a clicked link is reported like the link.** WKWebView asks about the
  redirect with the same navigation type and mouse button, so `IsUserInitiated` is `true` for it.
  WebKit has `_isRedirect` and `_isUserInitiated` on `WKNavigationAction`, which are private and were
  left alone. A way that stays public, not tried: let a click or a key press that the view receives
  (`mouseDown:`, `keyDown:` of `NeoAstraWebView`) allow one user-initiated request, so that the
  redirect that follows without new input is not one.
- **A navigation request carries no modifier keys or mouse button, and the context menu is all or
  nothing.** Both were asked for with the changes above and left out. WKWebView (`modifierFlags`,
  `buttonNumber`) and WebKitGTK (`webkit_navigation_action_get_modifiers`,
  `webkit_navigation_action_get_mouse_button`) report the first, and WebView2 has nothing on
  `NavigationStarting` or `NewWindowRequested`, so it would be a value of two platforms. The items
  of the native context menu need `ContextMenuRequested` of WebView2, `willOpenMenu:` on macOS, and
  the `context-menu` signal of WebKitGTK, with a portable list of items that does not exist yet.
- **Linux: every navigation request is reported for the main frame.** `decide_policy` in
  `native/src/linux/gtk_backend.cpp` sets `NEOASTRA_NAVIGATION_REQUEST_MAIN_FRAME` without looking at
  the frame, where the Cocoa backend reads `targetFrame.isMainFrame` and WebView2 raises
  `NavigationStarting` for the top-level document only. WebKitGTK asks about the navigations of
  subframes through the same signal, so `NeoNavigationRequest.IsMainFrame` is `true` for them, and
  the policy of `NeoApp`, which keeps subframe links away from the system browser by that flag, does
  not on Linux. Read in the source on a Mac, not run. To do: see what WebKitGTK 6.0 tells about the
  frame of a `WebKitNavigationAction` and set the bit from it, or document the difference.

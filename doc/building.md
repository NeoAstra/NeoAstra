# Building and verification

This guide covers development from a source checkout. For package-based applications, follow the
[consumer setup guide](frontend-tooling-and-assets.md#consumer-path-create-run-develop-publish).

## Prerequisites

- The .NET 10 SDK, selected by [`src/global.json`](../src/global.json).
- Node.js and npm to compile the repository's frontend SDK and advanced sample. CI uses Node.js 22.
  Normal source builds perform an incremental locked npm restore when required.
- A graphical desktop and the [platform runtime dependencies](platform-support.md#runtime-requirements)
  to run samples or browser checks.
- Python 3.13 for the engineering tests and Python 3.12 for the native build helper (the CI
  versions); CMake, Clang, and the platform development dependencies when rebuilding native code.
  See the [native workflow](../.github/workflows/native.yml) for host-specific setup.

## Managed build and tests

From the repository root:

```sh
cd src
dotnet build -c Release
dotnet test -c Release
```

The native ABI is `1.0`. The managed loader and the bundler check its major version only, so the checked-in native assets can be regenerated without blocking local applications.

## Samples and NativeAOT

From the repository root, run the HelloWorld sample:

```sh
dotnet run --project samples/NeoAstra.Sample -c Release
```

The [HelloWorld](../samples/NeoAstra.Sample), [Core sample](../samples/NeoAstra.Core.Sample), and
[advanced feature tour](../samples/NeoAstra.Sample.Advanced/readme.md) are configured for NativeAOT.
Publish the HelloWorld with:

```sh
dotnet publish samples/NeoAstra.Sample/NeoAstra.Sample.csproj -c Release -r win-x64 --self-contained
```

The Core sample's `--validate-native-library` option performs a non-interactive native load and dispatcher-detach smoke check without creating a browser view.

## Browser conformance and benchmarks

The browser conformance and performance executables are built with the solution but are never run by a normal build or test. Both are noninteractive and opt-in; invoking either without arguments exits successfully without creating an application or browser.

From the repository root, run local conformance with:

```sh
dotnet run --project src/NeoAstra.Conformance -c Release -- --run
```

Add `--stress` for the bounded high-volume scenarios or `--timeout-seconds N` to change the per-scenario limit. The harness uses only copied `conformance://` fixtures and prints an explicit `SKIP` when a backend capability, trusted user activation, destructive process failure, filesystem mutation, or subprocess isolation prevents safe automation.

The `automation:` scenarios call the [browser automation](browser-automation.md) tools by name against a fixture whose content security policy allows no inline script. Unlike the other scenarios, each of them reports its own `FAIL` line and the following ones still run, so that one run shows everything that differs on a platform; the run fails at the end when any of them did. They keep their windows hidden, except for the last one, which shows its window to capture it, to click at coordinates, and to resize it.

Two tests of `WindowsDropTargetTests` drag a page element with the real mouse of a Windows desktop, within a window and to another one, and are skipped unless `NEOASTRA_TEST_REAL_INPUT` is `1`. They take the pointer for about two seconds each, drive it from a thread of their own, release the button in every case, and let go when someone else moves the mouse. Keep their windows uncovered while they run:

```sh
NEOASTRA_TEST_REAL_INPUT=1 dotnet test src/NeoAstra.Tests -c Release --filter "FullyQualifiedName~RealMouseDrag"
```

Run the dependency-free benchmark harness with:

```sh
dotnet run --project src/NeoAstra.Benchmarks -c Release -- --run --quick
```

Omit `--quick` for the bounded default sample, or use `--iterations`, `--lifecycle-iterations`, `--timeout-seconds`, and `--idle-seconds` to tune it. Add `--rpc-only` to run only the RPC host measurements (small invoke latency and allocation, concurrent invokes, event and channel throughput) over in-memory sessions: no application, window or browser is created, and the throughput measurements send through a simulated UI thread whose turns cost about 20 microseconds each, as a view binding sends through the UI thread. They also measure frames through the transport of a view without its browser, everything but the native calls that hand a message to the browser or give one: a command that answers 1 MB (time, allocation, and the time its frame holds the UI thread), the authentication of a small and of a 1 MB frame of the page on the UI thread (time and allocation), and a command that receives 1 MB. Without `--rpc-only`, the browser run also sends RPC frames from the host to the fixture page through a view binding and the native post of the view: small events of a page subscription (`rpc events to a page`) and 200 KiB results of calls the page sends together (`rpc 200 KiB results to a page`). Each `RESULT`/`SKIP` identifies the backend and platform. Results include browser-engine, native-backend, OS-scheduling, and machine effects; environment/view timing must not be interpreted as NeoAstra controlling engine startup, and memory/idle-CPU figures currently cover only the host process. Use same-machine, same-engine regression baselines rather than absolute comparisons across platforms.

## Native builds

The native library uses CMake presets and Clang. From the repository root, the build helper selects a .NET RID, runs the native tests, and stages the resulting library in `src/NeoAstra.Core/runtimes/<RID>/native`:

```sh
python eng/build_native.py --rid win-x64 --clean
```

The managed project directly copies the staged library for the current host RID beside its development output, so local applications and tests use the latest native build without creating or installing a NuGet package. Rerun the helper after native changes. The same command accepts `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, and `osx-arm64`; use `--skip-tests` when cross-compiling a binary that cannot run on the build host.

Native tests include a public-header ABI test, common ownership tests, a bounded dispatch-drain test (order, one callback per elapsed slice, callbacks that keep queuing, quit), contended dispatch/UI-object teardown stress tests, and an independent frozen ABI 1.7 consumer. The frozen consumer does not include the current header: it loads the shared library by path, verifies every ABI 1.7 export, calls core entry points through frozen C-layout declarations, and performs an attach/detach ownership cycle. Compatible exports added after ABI 1.7 do not change that export floor. On macOS and Linux the native tests also check which WebKit data store or network session each user-data root and profile selects; those tests create browser views and use WebKit's cookie storage, but load no page. On Linux they check as well which GTK renderer the application chooses and that the applications it opens addresses with do not get it; in a Wayland session with the NVIDIA driver and GTK 4.16 or later, they check that a window draws with GL without a warning of GTK, and say that they skip it elsewhere. They check that a destroyed view leaves its window, and that the settings of a view for the Tab key and for history navigation reach WebKitGTK. On macOS a further test loads `data:` pages in a hidden window and checks that a view reports whether it can go back and forward as its history changes, that a navigation request tells a new document, a reload, and an entry of the history apart, and that a view whose history navigation is turned off stays on its page and still reloads it; another one hands key presses and clicks to such a view: it checks that the Tab key stops on a link only while the view is set to, that a link is reported as user-initiated when the user activates it and not when a script does, and that an address sent outside the view reaches the system only when it is a web address. That test takes the place of `NSWorkspace`, so no browser opens. A last one zooms such a view and checks that its page is laid out again in a viewport of the size of the view divided by the zoom and still fills the view, and that a view keeps its zoom from before its first document on, through a new document and a reload.

On Windows, `neoastra_windows_callback_tests` sends native window and dispatcher messages while event handlers release their last window/application handle or detach the application. It checks that the rest of the callback remains valid, and that already-zero-reference owners still receive native destruction without being revived or publishing user events. Window creation, bounds, visibility, and fullscreen-exit checks also detach from nested resize events, verifying that platform operations stop when their native state has been destroyed. The test executable has an icon group whose first picture is 16 pixels: the test checks that a window shows that icon at the sizes of the scale of its display, takes it again at the sizes of another scale on `WM_DPICHANGED`, keeps an icon the application gave it through such a change, and shows the icon of the executable again when the application takes its own away. It also checks that releasing one of two modal siblings leaves their owner disabled until the other closes, including application shutdown with a surviving modal handle. It queues dispatcher callbacks that take longer together than one drain slice and checks that one dispatch message stands for the queue, that the callbacks run in order over several returns to the message loop, that a window waiting to be painted is painted before the rest of the queue runs, that a message loop inside a callback still runs the callbacks queued behind it, and that an empty queue arms no further wake-up. Run it on a Windows host after configuring and building `windows-x64-debug`:

```sh
ctest --preset windows-x64-debug -R '^neoastra_windows_callback_tests$'
```

This focused test needs no browser view; it does not cover WebView2 event, download, or OLE drag callbacks. Those paths still require Windows/WebView2 runtime validation; a passing macOS or Linux common-ownership test does not exercise the Windows backend.

The source-backed deferred-download regression can run on any host with Clang:

```sh
python3 -m unittest eng.tests.test_windows_download_lifetime -v
```

It compiles the production download decision context, completion, construction, and retain guard
against portable COM/teardown stand-ins. It checks that a deferred decision keeps its download alive
through lifecycle teardown and COM cleanup without a client retain. This is not Windows backend
compilation, WebView2 execution, or a real application-detach test.

The [mobile samples](../samples/mobile/readme.md) host one page and one generated RPC service on iOS and Android with the libraries below.

### iOS native library (preview)

The iOS backend is a static library, because an iOS application cannot load a library of its own at run time. An application attaches to the run loop of UIKit instead of running one, and it hosts a view either in a window of NeoAstra or in a `UIView` that it owns: `NeoApplication.CreateWindow` creates a `UIWindow` in the active window scene (call it once a scene is connected), which fills the scene, takes its title as the title of the scene, and closes with it, and `NeoAstraHost.FillWindow` puts a view in it; `NeoAstraHost.FromUIKitUIView` borrows a view of the application instead. The WKWebView does not inset its content for the status bar or the home indicator itself: a page laid out with `viewport-fit=cover` keeps clear of them through `env(safe-area-inset-*)`, the same on every iOS version (iOS 26 would otherwise move the page down by the status bar a second time). A window cannot be moved, resized, minimized, or owned, so those calls report `NEOASTRA_ERROR_NOT_SUPPORTED`. Custom schemes, document-start and document-end scripts, authenticated messages with the origin of the sender, navigation decisions, new-window decisions, ephemeral profiles, and viewport capture work; the capability query says what is missing. A tapped link counts as a navigation that the user started, and an allowed new window loads in the view that asked, since no popup view is created. WKWebView has no JavaScript dialogs of its own: `alert`, `confirm`, and `prompt` reach `ScriptDialogRequested`, and without a handler an alert is accepted and the others are canceled, as on macOS. A request for a camera or a microphone reaches `PermissionRequested`, whose default answer lets WebKit ask the user (the application declares `NSCameraUsageDescription` or `NSMicrophoneUsageDescription`); the iOS simulator offers no `getUserMedia`. A file input shows the picker of WebKit itself, since iOS asks no host for it. Popup views, downloads, cookies, and zoom report `NEOASTRA_ERROR_NOT_SUPPORTED`. Build the library on macOS with Xcode:

```sh
cmake --preset iossimulator-arm64-release && cmake --build artifacts/native/iossimulator-arm64-release
cmake --preset ios-arm64-release && cmake --build artifacts/native/ios-arm64-release
```

The presets build no tests, since an iOS library cannot run on the host. `python eng/build_native.py --rid ios-arm64` (or `iossimulator-arm64`) builds the library, checks its exports against the header, and stages it in `src/NeoAstra.Core/runtimes/<RID>/native` like a desktop library. The library names the system frameworks that it needs, so the linker of an application finds them even when the application trims its own bindings. The `NeoAstra.Core` package does the rest for an iOS application: its `buildTransitive` targets link the library of the runtime identifier statically and declare every export that `NeoAstra.Core` imports, because the linker of an iOS application removes what no code references and strips the rest from a device build. `NeoAstra.CodeGen` writes that list, `src/NeoAstra.Core/Build/Sdk/NeoAstra.Core.NativeSymbols.props`, with the generated interop. `NeoAstra.Core` resolves its imports in the main program on iOS (`NativeLibrary.GetMainProgramHandle`). A project that references `NeoAstra.Core` as a project instead of a package imports `src/NeoAstra.Core/Build/Sdk/NeoAstra.Core.targets` itself. WKWebView ends the web content process of a view under memory pressure; the application recreates the view when `ProcessFailed` reports it, as on the desktop. The keyboard of a WKWebView differs from the one of a native application, and three MSBuild properties change it for every view of the application; each merges an `Info.plist` entry (under `com.neoastra.webview.`) that a view reads when it is created, and an application may set the entry in its own `Info.plist` instead. `NeoAstraIOSHideFormAccessoryBar=true` drops the bar with the previous, next, and done buttons above the keyboard. `NeoAstraIOSKeyboardWithoutUserAction=true` lets `focus()` open the keyboard when the user did not just tap the page, for example when a sheet opens with its first field focused. `NeoAstraIOSKeyboardOverlay=true` (on Android `NeoAstraAndroidKeyboardOverlay=true`) keeps WebKit from insetting and scrolling the page when the keyboard comes up, so the keyboard lies over the page as over the screen of a native application; the page learns how much of the view the keyboard covers from the CSS variable `--neoastra-keyboard-height` on the root element (`0px` once it is hidden) and from a `neoastrakeyboard` event on `window` whose `detail` holds the `height` in CSS pixels and the `duration` of the animation of the keyboard in seconds, and it keeps its fields clear of the keyboard itself. `onKeyboard(handler)` of `@neoastra/client` calls the page with each `{ height, duration }` and returns the function that stops it, and `keyboardHeight()` reads the variable. WebKit has no public switch for any of the three, so they change `WKContentView` the way the keyboard plugins of Cordova and Capacitor do; on a version of iOS whose WebKit no longer has what they change, a switch does nothing.

### Android native library (preview)

The Android backend is a shared library for the `android-arm64` and `android-x64` RIDs, built with the Android NDK (r29 or later keeps the 16 KB page alignment that recent Android versions ask for). Point `ANDROID_NDK_HOME` at the NDK and use the presets:

```sh
export ANDROID_NDK_HOME="$HOME/Library/Android/sdk/ndk/29.0.14206865"
cmake --preset android-arm64-release && cmake --build artifacts/native/android-arm64-release
cmake --preset android-x64-release && cmake --build artifacts/native/android-x64-release
```

An application attaches to the native looper of the calling thread and hosts a view either in a window of NeoAstra or in a `ViewGroup` that it owns. `NeoApplication.CreateWindow` takes the current activity (call it in `OnCreate` or later), whose content it replaces with a layout that keeps clear of the system bars; with the MSBuild property `NeoAstraAndroidEdgeToEdge=true` the targets merge an application meta-data entry (`com.neoastra.window.edgeToEdge`) and the window draws under the transparent system bars instead, while the WebView reports their size to the page as `env(safe-area-inset-*)`, so a page laid out for iOS (`viewport-fit=cover`) keeps clear of them the same way. Android pans such a window to show a focused field, which moves the whole page under the status bar; with the MSBuild property `NeoAstraAndroidKeyboardOverlay=true` the targets merge the meta-data entry `com.neoastra.webview.keyboardOverlay`, the window neither pans nor resizes, every page of the view sets `navigator.virtualKeyboard.overlaysContent` and the WebView receives its insets without the keyboard, so that Chromium neither shrinks nor scrolls its visual viewport (the system bars stay in the insets), and the page learns how much of the view the keyboard covers as on iOS, from `--neoastra-keyboard-height` and the `neoastrakeyboard` event (the duration is the one of the animation of the keyboard, and 0 when Android shows it without one). Android needs no switch for the form bar or for `focus()`, which opens the keyboard without a tap. One activity has one window, which takes the title of the activity, ends with it, and asks to close on back from Android 13. An activity that handles its configuration changes itself keeps its window when the device rotates. `NeoAstraHost.FillWindow` puts a view in the window; `NeoAstraHost.FromAndroidViewGroup` borrows a view group of the application instead. A window cannot be moved, resized, minimized, or owned, so those calls report `NEOASTRA_ERROR_NOT_SUPPORTED`. The view is an Android System WebView that the Java class `com.neoastra.backend.NeoAstraWebHost` creates, which needs AndroidX WebKit (`Xamarin.AndroidX.WebKit`) for web message listeners and document-start scripts. Android has no custom schemes, so `app://neoastra` is served to the WebView as `https://neoastra.app.invalid`, an origin under the reserved `.invalid` domain that never reaches the network; the managed side, the bridge origins, and the RPC context still see `app://neoastra`, while `location.origin` in the page is the projected one. Document-start scripts, authenticated messages with the origin of the sender, history, title, navigation decisions, new-window decisions, and the loss of the renderer work. Android asks about a navigation synchronously, so a decision that the host makes later cancels the navigation first and then loads the allowed address in the main frame (a frame other than the main one stays where it is); a new window is decided with the address of its first navigation, and an allowed one loads in the view that asked, since no popup view is created. The WebView shows its own JavaScript dialogs until `ScriptDialogs` is off or a `ScriptDialogRequested` handler is set; the host then answers them. A file input asks `FileChooserRequested`: the default answer shows the picker of the system, or the camera for an image input with the `capture` attribute (the photo goes to a file in the cache folder `neoastra-camera` that a file provider of NeoAstra, `${applicationId}.neoastra.files`, shares with the camera; an application that declares `android.permission.CAMERA` is asked for it first), chosen paths go to the page (a document of the picker as a copy in the cache folder `neoastra-chooser`, made while NeoAstra's result activity still holds the read permission that the picker gave it), and without a handler the input is canceled; a `NeoApp` gives the default answer with `UseSystemFileChooser()`. A request for a camera or a microphone asks `PermissionRequested`; an allowed or default answer also asks the user for the runtime permission of the application when it does not hold it yet (the application declares `android.permission.CAMERA` or `android.permission.RECORD_AUDIO`). The picker and the permission requests, those for a camera or a microphone and the one for notifications, run in a translucent activity of NeoAstra, `com.neoastra.backend.NeoAstraResultActivity`, which the manifest of the package declares and which receives the answer of the system. Document-end scripts, private environments, ephemeral profiles, popup views, downloads, cookies, zoom, and capture report `NEOASTRA_ERROR_NOT_SUPPORTED`. The presets build no tests. With `ANDROID_NDK_HOME` set, `python eng/build_native.py --rid android-arm64` (or `android-x64`) builds the library, checks its exports with the LLVM tools of the NDK, and stages it in `src/NeoAstra.Core/runtimes/<RID>/native`. The `NeoAstra.Core` package does the rest for an Android application: its targets package the library of each ABI, compile the Java side, merge a content provider that loads it when the process starts (only a load through Java runs the `JNI_OnLoad` of the library), and give R8 the rules that keep what the library reaches through JNI. The same content provider initializes `com.neoastra.backend.NeoAstraServices`, the Java window class and the Java services (clipboard, safe storage, theme, opener, notifications, dialogs, and the copy of the packaged frontend assets) that `neoastra_platform_call` reaches. An application that shows notifications declares `android.permission.POST_NOTIFICATIONS` in its manifest; NeoAstra asks for it on the first display on Android 13 and later. A notification scheduled for later (`DeliverAt`) is posted by the receiver `com.neoastra.backend.NeoAstraNotificationReceiver`, which the manifest of the package declares: an application declares `android.permission.USE_EXACT_ALARM` (or `SCHEDULE_EXACT_ALARM`) for the exact moment, and `android.permission.RECEIVE_BOOT_COMPLETED` to have its notifications armed again after a restart. The application references `Xamarin.AndroidX.WebKit` itself. A project that references `NeoAstra.Core` as a project instead of a package imports `src/NeoAstra.Core/Build/Sdk/NeoAstra.Core.targets` itself. .NET 11 supports no 32-bit x86 Android target.

## Frontend checks

From the repository root, build and verify the frontend package, CSP/bootstrap fixtures, framework consumers, size budget, licenses, provenance, and publish contents with:

```sh
cd frontend
npm ci
npm run check
```

`npm run bench` builds the package and measures the page side of the transport, the real bootstrap
and the built client without a browser, on a frame of 10,000 objects in each direction
(`BENCH_ITEMS` and `BENCH_ITERATIONS` change the size and the number of runs).

## CI and release workflows

The [CI workflow](../.github/workflows/ci.yml) builds and tests on macOS and Linux, then runs
`dotnet-releaser` on Windows to build, test, and pack using [the release configuration](../src/dotnet-releaser.toml).
The shared action uses its default `run` mode: pull requests and branch pushes validate without publishing;
release tag pushes automatically publish NuGet packages through trusted publishing. The NuGet account
`xoofx` must have a trusted publishing policy for `NeoAstra/NeoAstra` and workflow `ci.yml`; no NuGet API-key
secret is required. Packages use the checked-in native RID assets. The manually dispatched
[`package.yml` workflow](../.github/workflows/package.yml) rebuilds all six native RIDs and runs the extended package checks without publishing.

Native CI is configured with explicit `linux-x64-asan-ubsan`, `linux-x64-analysis`, and `macos-x64-asan-ubsan` presets. The `linux-x64-tsan` preset remains available for manual investigation, but its CI job is temporarily disabled because uninstrumented GTK/Pango/GLib/GIO worker synchronization produces changing false-positive reports. The ThreadSanitizer test preset selects only the common ownership and contended teardown tests; it does not run browser conformance automation. Sanitizer test presets use fail-fast runtime options, instrument the test executables and shared library, and disable LTO. LeakSanitizer remains disabled until process-global allocations in GTK/WebKitGTK and Apple WebKit have reviewed suppressions. The enabled sanitizer jobs describe configured coverage, not evidence that they ran on a Windows development host; run the matching preset on its named host to establish an execution result. ThreadSanitizer remains separate from AddressSanitizer and UndefinedBehaviorSanitizer.

See [platform support](platform-support.md) for the distinction between configured coverage and runtime validation.

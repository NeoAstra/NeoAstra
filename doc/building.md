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

Run the dependency-free benchmark harness with:

```sh
dotnet run --project src/NeoAstra.Benchmarks -c Release -- --run --quick
```

Omit `--quick` for the bounded default sample, or use `--iterations`, `--lifecycle-iterations`, `--timeout-seconds`, and `--idle-seconds` to tune it. Each `RESULT`/`SKIP` identifies the backend and platform. Results include browser-engine, native-backend, OS-scheduling, and machine effects; environment/view timing must not be interpreted as NeoAstra controlling engine startup, and memory/idle-CPU figures currently cover only the host process. Use same-machine, same-engine regression baselines rather than absolute comparisons across platforms.

## Native builds

The native library uses CMake presets and Clang. From the repository root, the build helper selects a .NET RID, runs the native tests, and stages the resulting library in `src/NeoAstra.Core/runtimes/<RID>/native`:

```sh
python eng/build_native.py --rid win-x64 --clean
```

The managed project directly copies the staged library for the current host RID beside its development output, so local applications and tests use the latest native build without creating or installing a NuGet package. Rerun the helper after native changes. The same command accepts `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, and `osx-arm64`; use `--skip-tests` when cross-compiling a binary that cannot run on the build host.

Native tests include a public-header ABI test, common ownership tests, a bounded dispatch-drain test (order, one callback per elapsed slice, callbacks that keep queuing, quit), contended dispatch/UI-object teardown stress tests, and an independent frozen ABI 1.7 consumer. The frozen consumer does not include the current header: it loads the shared library by path, verifies every ABI 1.7 export, calls core entry points through frozen C-layout declarations, and performs an attach/detach ownership cycle. Compatible exports added after ABI 1.7 do not change that export floor. On macOS and Linux the native tests also check which WebKit data store or network session each user-data root and profile selects; those tests create browser views and use WebKit's cookie storage, but load no page. On Linux they check as well that a destroyed view leaves its window, and that the settings of a view for the Tab key and for history navigation reach WebKitGTK. On macOS a further test loads `data:` pages in a hidden window and checks that a view reports whether it can go back and forward as its history changes, that a navigation request tells a new document, a reload, and an entry of the history apart, and that a view whose history navigation is turned off stays on its page and still reloads it; another one hands key presses and clicks to such a view: it checks that the Tab key stops on a link only while the view is set to, that a link is reported as user-initiated when the user activates it and not when a script does, and that an address sent outside the view reaches the system only when it is a web address. That test takes the place of `NSWorkspace`, so no browser opens. A last one zooms such a view and checks that its page is laid out again in a viewport of the size of the view divided by the zoom and still fills the view, and that a view keeps its zoom from before its first document on, through a new document and a reload.

On Windows, `neoastra_windows_callback_tests` sends native window and dispatcher messages while event handlers release their last window/application handle or detach the application. It checks that the rest of the callback remains valid, and that already-zero-reference owners still receive native destruction without being revived or publishing user events. Window creation, bounds, visibility, and fullscreen-exit checks also detach from nested resize events, verifying that platform operations stop when their native state has been destroyed. It also checks that releasing one of two modal siblings leaves their owner disabled until the other closes, including application shutdown with a surviving modal handle. It queues dispatcher callbacks that take longer together than one drain slice and checks that one dispatch message stands for the queue, that the callbacks run in order over several returns to the message loop, that a window waiting to be painted is painted before the rest of the queue runs, that a message loop inside a callback still runs the callbacks queued behind it, and that an empty queue arms no further wake-up. Run it on a Windows host after configuring and building `windows-x64-debug`:

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

## Frontend checks

From the repository root, build and verify the frontend package, CSP/bootstrap fixtures, framework consumers, size budget, licenses, provenance, and publish contents with:

```sh
cd frontend
npm ci
npm run check
```

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

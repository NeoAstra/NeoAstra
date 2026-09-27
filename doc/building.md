# Building and verification

This guide covers development from a source checkout. For package-based applications, follow the
[consumer setup guide](frontend-tooling-and-assets.md#consumer-path-create-run-develop-publish).

## Prerequisites

- The .NET 10 SDK, selected by [`src/global.json`](../src/global.json).
- Node.js and npm to compile the repository's frontend SDK and advanced sample. CI uses Node.js 22.
  Normal source builds perform an incremental locked npm restore when required.
- A graphical desktop and the [platform runtime dependencies](platform-support.md#runtime-requirements)
  to run samples or browser checks.
- Python for the engineering tests and native build helper; CMake, Clang, and the platform development
  dependencies when rebuilding native code. See the [native workflow](../.github/workflows/native.yml)
  for host-specific setup.

## Managed build and tests

From the repository root:

```sh
cd src
dotnet build -c Release
dotnet test -c Release
```

NeoAstra's pre-release native ABI remains `1.0`. Until the first release, the managed loader and bundler enforce only ABI major compatibility so existing pre-release RID assets can be regenerated without blocking local applications. Managed and RID-specific native assets will become a strictly paired release unit before release.

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

Native tests include a public-header ABI test, common ownership tests, contended dispatch/UI-object teardown stress tests, and an independent frozen ABI 1.7 consumer. The frozen consumer does not include the current header: it loads the shared library by path, verifies every ABI 1.7 export, calls core entry points through frozen C-layout declarations, and performs an attach/detach ownership cycle. Compatible exports added after ABI 1.7 do not change that export floor.

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

# Platforms and runtime dependencies

NeoAstra is pre-release software. The platforms below are the intended v1 support targets, not a
claim that every target has passed release-level runtime validation. In this document:

- **Implemented** means a native backend and RID packaging path exist in source.
- **Workflow coverage** means the repository configures a build, test, or publish job; it does not
  prove that a particular workflow run passed.
- **Runtime validated** means a real application created a browser view on that operating system and
  architecture. No macOS or Linux runtime-validation result is asserted here.

## Support and validation matrix

| Operating system | Architecture / RID | Browser backend | v1 intent | Repository status |
| --- | --- | --- | --- | --- |
| Windows 10/11 | x64 / `win-x64` | WebView2 | Required | Implemented. Native and managed CI, NativeAOT publish, native-load smoke coverage, and Windows-only live-browser test code are configured. |
| Windows 10/11 | ARM64 / `win-arm64` | WebView2 | Required | Implemented. Native asset cross-build/package coverage is configured; native execution is skipped and no ARM64 browser run is established. |
| macOS | x64 / `osx-x64` | WKWebView | Required | Implemented and source-reviewed. Native build/test coverage is configured; no release-level browser-runtime result is asserted. The minimum supported macOS version is not yet frozen. |
| macOS | ARM64 / `osx-arm64` | WKWebView | Required | Implemented and source-reviewed. Native and managed CI plus NativeAOT publish coverage are configured; no release-level browser-runtime result is asserted. The minimum supported macOS version is not yet frozen. |
| Ubuntu 24.04+ | x64 / `linux-x64` | WebKitGTK 6.0 | Required | Implemented and source-reviewed. Native tests under Xvfb, managed CI, NativeAOT publish, and native hardening jobs are configured; no release-level browser-runtime result is asserted. |
| Ubuntu 24.04+ | ARM64 / `linux-arm64` | WebKitGTK 6.0 | Required | Implemented and source-reviewed. Native build/test/package coverage on ARM64 infrastructure is configured; no release-level browser-runtime result is asserted. |
| Linux using musl | x64 or ARM64 | WebKitGTK | Not a v1 target | Unsupported. No musl RID assets, compatible dependency baseline, or musl runtime CI are present. |

Thirty-two-bit architectures and RIDs other than those listed above are not supported. The .NET 10
`NeoAstra.Core` package carries the native assets; the ordinary `NeoAstra` application package depends
on that core. The packaging workflow assembles all six native assets; a workflow definition alone is
not a release-validation record.

## Runtime requirements

All platforms require:

- A .NET 10 runtime, unless the application is published self-contained or with NativeAOT.
- A NeoAstra native asset matching the operating system and process architecture. The pre-release
  ABI is `1.0`, and the current loader/bundler enforce major compatibility only. Strict managed/native
  release pairing remains a release gate, not a control already enforced by the pre-release loader.
- A graphical desktop session and use of the platform UI thread. Headless build success does not
  establish that a browser view can be created.

### Windows

- Windows 10 or Windows 11 on x64 or ARM64 is the v1 target.
- The Microsoft Edge WebView2 Runtime must be installed for the process architecture, or the host
  must explicitly provide a compatible fixed-version runtime path through NeoAstra environment
  options.
- NeoAstra uses the statically linked WebView2 loader, but it does **not** bundle the WebView2
  browser runtime or another Chromium distribution.

### macOS

- A system-provided Cocoa and WebKit/WKWebView implementation is required; NeoAstra does not
  bundle a browser engine.
- Both x64 and ARM64 native assets are implemented. The repository does not yet declare a minimum
  supported macOS release, so a distributable application must set and validate its own deployment
  target before claiming support.
- Browser features still depend on the WKWebView APIs available in the user's macOS release. See
  [known limitations](known-limitations.md) and query `NeoEnvironment.GetCapability` before exposing
  optional UX.

### Linux

- The initial target is Ubuntu 24.04 or later using glibc, with WebKitGTK API 6.0, GTK 4, libsoup 3,
  GLib, and their transitive runtime libraries supplied by the distribution.
- The build uses the `gtk4` and `webkitgtk-6.0` pkg-config modules. Repository CI installs
  `libgtk-4-dev` and `libwebkitgtk-6.0-dev` to compile; deployed applications need the corresponding
  distribution runtime libraries, not necessarily the development packages.
- On Ubuntu 24.04, install the direct runtime packages with
  `sudo apt-get install libgtk-4-1 libwebkitgtk-6.0-4`. APT installs the required
  libsoup 3, GLib, and other transitive libraries.
- GTK must be able to connect to an X11 or Wayland display. Backend initialization fails when no
  usable display is available.
- Other glibc distributions may work when they provide ABI-compatible GTK 4 and WebKitGTK 6.0
  libraries, but they are not part of the current v1 support intent. Alpine and other musl systems
  are unsupported.

## What is and is not currently validated

The main CI workflow builds and tests `NeoAstra.slnx` on Windows x64, macOS ARM64, and Linux x64.
A separate native workflow runs automatically only when native sources, build inputs, or staged native
runtimes change; it compiles artifacts for all six RIDs and executes native tests on all except Windows
ARM64. NativeAOT, packaging, delivery, and desktop conformance remain separately dispatchable workflows
rather than checks on every managed change. The native tests cover the ABI, ownership, dispatch,
teardown, and stress behavior; they do not by themselves prove end-to-end browser behavior.

Normal CI does not invoke the browser harness with `--run`. The separate, manually dispatched
`conformance.yml` now configures both desktop-service smoke and browser `--run --stress` execution on
Windows x64, macOS x64, and Linux x64, with per-step deadlines and retained logs including explicit
skips. This is configured coverage, not a report that those jobs passed, and does not cover every RID.
Ordinary managed CI also runs the dependency-free engineering tests, including ABI-header parsing.

This page does not infer macOS or Linux runtime support from source review, compilation, native tests,
Xvfb use, or NativeAOT publication. A v1 support claim still requires the platform sample and browser
integration acceptance criteria to be run and recorded on each target. Record the OS/engine/RID,
artifact identity, command, and pass/fail/skip results; review each skip against release requirements.

For local verification, see the sample and conformance commands in the
[project readme](../readme.md#building). Passing those commands on one machine validates that tested
environment only; it does not expand the support matrix.

# Platforms and runtime dependencies

NeoAstra shows the browser engine of the platform and does not bundle one. The `NeoAstra.Core`
package, which the `NeoAstra` application package depends on, carries a native library for each of
these targets:

| Operating system | Architecture / RID | Browser engine | Checked with |
| --- | --- | --- | --- |
| Windows 10 and 11 | x64 / `win-x64` | WebView2 | Native tests, unit tests that drive a live browser, and the conformance harness |
| Windows 10 and 11 | ARM64 / `win-arm64` | WebView2 | Cross-built only; not run on ARM64 hardware |
| macOS 15 or later | x64 / `osx-x64` | WKWebView | Native tests and the conformance harness |
| macOS 26 or later | ARM64 / `osx-arm64` | WKWebView | Native tests, and the conformance harness on a development machine |
| Ubuntu 24.04 or later | x64 / `linux-x64` | WebKitGTK 6.0 | Native tests and the conformance harness |
| Ubuntu 24.04 or later | ARM64 / `linux-arm64` | WebKitGTK 6.0 | Native tests; no browser run |

Linux distributions that use musl, such as Alpine, 32-bit architectures, and other RIDs are not
supported.

The engines do not offer the same features. [Known limitations](known-limitations.md) lists what
differs, and `NeoEnvironment.GetCapability` tells an application what the engine it runs on supports.

## Runtime requirements

Every platform needs:

- A .NET 10 runtime, unless the application is published self-contained or with NativeAOT.
- A graphical desktop session. Application and browser operations start on the UI thread of the
  platform.

### Windows

- The Microsoft Edge WebView2 Runtime, installed for the architecture of the process. Windows 11
  includes it. A host can instead name a fixed-version runtime through the environment options.
- NeoAstra links the WebView2 loader statically. It does not ship the WebView2 runtime or another
  Chromium distribution.
- The Microsoft Visual C++ runtime (`MSVCP140.dll`, `VCRUNTIME140.dll`) for the architecture of the
  process, which `neoastra_native.dll` imports. Most machines have it; a clean one needs the
  [latest Microsoft Visual C++ Redistributable](https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist),
  or the DLLs copied beside the application.

### macOS

- Nothing to install: WKWebView is part of the system.
- A native library loads on the system it was built for and on later ones. The libraries of this
  package are built on macOS 15 for x64 and on macOS 26 for ARM64, so an application on Apple silicon
  needs macOS 26.
- Browser features depend on the WKWebView of the user's macOS release. Query
  `NeoEnvironment.GetCapability` before offering an optional feature.

### Linux

- Ubuntu 24.04 or later, with glibc, GTK 4, and WebKitGTK 6.0 from the distribution:
  `sudo apt-get install libgtk-4-1 libwebkitgtk-6.0-4`. APT installs libsoup 3, GLib, and the other
  libraries they need.
- An X11 or Wayland display that GTK can connect to. The backend fails to start without one.
- Another glibc distribution may work when it provides compatible GTK 4 and WebKitGTK 6.0 libraries,
  but none is checked.
- WebKitGTK runs its web process in a sandbox that needs unprivileged user namespaces. Where a system
  restricts them, pages do not load. That was met on the Ubuntu 24.04 runner of GitHub Actions, not in
  a desktop session.

## Troubleshooting

### The native library does not load

`NeoApplication.Run` and `NeoApplication.AttachToCurrentThread` load the native library first. NeoAstra
tries, in order, the path in the `NEOASTRA_NATIVE_LIBRARY` environment variable, the application
directory, `runtimes/<rid>/native/` below it, and the development build directories of the
repository; then the .NET runtime tries its own default places. When nothing loads, a
`NeoAstraNativeLibraryException` is thrown. Its message gives:

- the runtime identifier, architecture, and operating system of the process;
- every path tried, with what went wrong and the error of the platform loader: the text of
  `dlerror()` on Linux and macOS, the Win32 error code and its message on Windows;
- what to do, for each kind of failure that was met.

The same details are in the properties of the exception: `Probes` (one `NeoNativeLibraryProbe` for
each path, with its `Result`, `Error`, `NativeErrorCode`, `ImageArchitectures`, and
`MissingDependencies`), `Guidance`, `RuntimeIdentifier`, `ProcessArchitecture`, and
`OperatingSystemDescription`. Log the whole message: the cause is in it.

| What the message says for a path | Cause | Fix |
| --- | --- | --- |
| `not found` for every path, and `No usable neoastra_native.dll was found` | The library is not beside the application, for example in a framework-dependent build that has no RID, or a deployment that left out `runtimes/` | Reference `NeoAstra.Core` (the `NeoAstra` package references it), publish for a supported RID (`dotnet publish -r win-x64`), or set `NEOASTRA_NATIVE_LIBRARY` to the full path of the library |
| `missing dependency MSVCP140.dll, VCRUNTIME140.dll (Win32 error 126)` | Windows: the Visual C++ runtime is not installed | Install the [latest Microsoft Visual C++ Redistributable](https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist) for the architecture of the process, or copy the DLLs beside the application |
| `missing dependency libwebkitgtk-6.0.so.4` (or `libgtk-4.so.1`, `libjavascriptcoregtk-6.0.so.1`, `libsoup-3.0.so.0`), with `cannot open shared object file` | Linux: GTK 4 or WebKitGTK 6.0 is not installed | `sudo apt-get install libgtk-4-1 libwebkitgtk-6.0-4` on Debian and Ubuntu; `sudo dnf install gtk4 webkitgtk6.0` on Fedora, which NeoAstra does not check. `ldd libneoastra_native.so \| grep "not found"` lists every missing library |
| `version 'GLIBC_2.xx' not found` | Linux: the glibc of the system is older than the one the library was built with | Use Ubuntu 24.04 or later, or another glibc distribution of the same age |
| `Library not loaded: ...`, or `built for macOS 26.0 which is newer than running OS` | macOS: the release is older than the one the library needs | Run the `osx-x64` library on macOS 15 or later and the `osx-arm64` library on macOS 26 or later |
| `built for X64, not Arm64` (Win32 error 11 or 193 on Windows, `incompatible architecture` on macOS) | The library is for another architecture than the process, for example an x64 library in an ARM64 process | Use `runtimes/<rid of the process>/native/`, publish for the architecture of the process, or run the application with the architecture of the library |
| `not a valid Windows native library` (or macOS, Linux) | The file is empty, truncated, a text file, or a library of another operating system | Restore the file from the `NeoAstra.Core` package |
| `NeoAstra ships native libraries for ... only; this process runs as linux-musl-x64 (X64)` | The RID of the process is not supported | Run the application as one of the RIDs in the table at the top of this page |

When the library loads but its ABI or its exports do not match the managed assemblies, the message
says from which path it was loaded: replace it with the library of the same `NeoAstra.Core` version.

### The browser does not start on Windows

The WebView2 Runtime is not needed to load the native library; it is looked for when the first
browser environment is created. Without it, that operation fails with a `NeoAstraException` whose
`Code` is `NeoErrorCode.RuntimeUnavailable` and whose message is `Microsoft Edge WebView2 Runtime is
unavailable. Install the Evergreen Runtime or configure BrowserRuntimePath.` Install the
[Evergreen WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) for the
architecture of the process, or set `NeoEnvironmentOptions.BrowserRuntimePath` to a fixed-version
runtime.

## How the platforms are checked

- **Native tests** run in the native workflow for every target except Windows ARM64. They cover the
  native interface, object ownership, dispatch, and teardown, and parts of the macOS and Linux
  backends.
- **Unit tests** run on Windows x64, macOS ARM64, and Linux x64 on every push. The tests that drive a
  live browser run on Windows only.
- **The conformance harness** drives a real browser view through navigation, scripts, storage,
  messaging, the request of a page for a microphone, and [browser automation](browser-automation.md). It passes on Windows 11 x64, on macOS 15
  x64, and on Ubuntu 24.04 x64, where a workflow runs it on request. Its runners have no camera, and
  only the macOS one has a microphone, so the capture of both was run on a Windows machine and that of
  a microphone also in a Linux desktop session.

[Building and verification](building.md) has the commands to run these checks on your own machine,
and [open follow-ups](open-follow-ups.md) lists what is still to check.

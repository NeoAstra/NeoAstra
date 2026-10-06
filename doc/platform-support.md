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

## How the platforms are checked

- **Native tests** run in the native workflow for every target except Windows ARM64. They cover the
  native interface, object ownership, dispatch, and teardown, and parts of the macOS and Linux
  backends.
- **Unit tests** run on Windows x64, macOS ARM64, and Linux x64 on every push. The tests that drive a
  live browser run on Windows only.
- **The conformance harness** drives a real browser view through navigation, scripts, storage,
  messaging, and [browser automation](browser-automation.md). It passes on Windows 11 x64, on macOS 15
  x64, and on Ubuntu 24.04 x64. A workflow runs it on request for Windows and macOS; on the Linux
  runner of that workflow a page does not load yet, so the Linux run was made in a desktop session.

[Building and verification](building.md) has the commands to run these checks on your own machine,
and [open follow-ups](open-follow-ups.md) lists what is still to check.

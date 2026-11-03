# NeoAstra [![ci](https://github.com/NeoAstra/NeoAstra/actions/workflows/ci.yml/badge.svg)](https://github.com/NeoAstra/NeoAstra/actions/workflows/ci.yml) [![NuGet](https://img.shields.io/nuget/v/NeoAstra.svg)](https://www.nuget.org/packages/NeoAstra/)

<img align="right" width="160px" height="160px" src="https://raw.githubusercontent.com/NeoAstra/NeoAstra/main/img/NeoAstra.png">

NeoAstra runs a .NET application with a web UI on the platform's own webview, for desktop and mobile.
It bundles no browser engine, connects .NET and JavaScript with typed RPC, and is ready for NativeAOT.

| Platform | Webview |
| --- | --- |
| Windows | WebView2 |
| macOS | WKWebView |
| Linux | WebKitGTK |
| iOS (preview) | WKWebView |
| Android (preview) | Android System WebView |

## ✨ Features

- **Your choice of frontend**: plain HTML/JavaScript, React, Vue, or another web framework.
- **Typed .NET ↔ JavaScript RPC** with generated bindings, events, and streaming.
- **One application for desktop and mobile**: the same frontend, RPC services, and capability document on every platform; `NeoApp.Run` runs a desktop application, and `NeoApp.AttachAsync` attaches an iOS or Android one to the run loop of the platform.
- **Native integration**: windows, menus, dialogs, clipboard, notifications (also scheduled), safe storage, and haptics on phones, with plugins for location, push messages, passkeys, and in-app purchases on iOS and Android.
- **Capability-based security**: the page reaches only the commands that a capability document grants it, with scopes and per-platform selectors.
- **Chromeless desktop windows**: web-rendered title bars that keep the native frame, snapping, and window controls.
- **Controlled local assets** without a localhost server, with explicit security boundaries.
- **Development tooling**: project templates for desktop (vanilla, React, Vue) and mobile, frontend builds, and `dotnet neoastra dev` against the development server on the desktop, the iOS simulator, or an Android device.
- **Browser automation**: the snapshot, input, navigation, and debugging tools of Chrome DevTools MCP for every platform browser, ready to expose through an MCP server.
- **.NET 10 and NativeAOT-friendly** design with source-generated interop and serialization; iOS and Android applications build with the .NET 11 SDK.

## 📖 User guide

See the [documentation](doc/readme.md) for getting started, samples, platform support, security, and building from source.

For iOS and Android, start with [the simple host on iOS and Android](doc/application-lifecycle-and-hosting.md#the-simple-host-on-ios-and-android-preview),
the [mobile samples](samples/mobile/readme.md) with their device checklist, and the notes on
[building the mobile native libraries](doc/building.md#ios-native-library-preview).

On macOS, [Web Inspector is a per-view opt-in](doc/chromeless-windows.md#macos-web-inspector): use Safari's
Develop menu (macOS 13.3+) or the best-effort Inspect Element context-menu item, not `⌥⌘I`.

Large UI/.NET transfers use runtime-sized payload budgets by default, with configurable smaller
[transport and RPC limits](doc/frontend-transport.md#lifecycle-and-limits).
Native operation cancellation and shutdown ownership are described in the
[lifecycle guide](doc/application-lifecycle-and-hosting.md#native-asynchronous-completion-ownership).

Download policies can choose a destination asynchronously, including on Linux; timeouts and failed
handlers safely cancel the request. See the [platform notes](doc/known-limitations.md#download-policies-on-linux).

## 🪪 License

This software is released under the [BSD-2-Clause license](https://opensource.org/licenses/BSD-2-Clause).
Release artifacts also require the applicable [third-party notices](THIRD-PARTY-NOTICES.md).

## 🤗 Author

Alexandre Mutel aka [xoofx](https://xoofx.github.io).

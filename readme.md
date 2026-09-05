# NeoAstra [![ci](https://github.com/NeoAstra/NeoAstra/actions/workflows/ci.yml/badge.svg)](https://github.com/NeoAstra/NeoAstra/actions/workflows/ci.yml) [![NuGet](https://img.shields.io/nuget/v/NeoAstra.svg)](https://www.nuget.org/packages/NeoAstra/)

<img align="right" width="160px" height="160px" src="https://raw.githubusercontent.com/NeoAstra/NeoAstra/main/img/NeoAstra.png">

NeoAstra is a desktop application framework for .NET that brings your web UI to native windows.
It uses the platform browser — WebView2 on Windows, WKWebView on macOS, and WebKitGTK on Linux — without bundling a browser engine.

> [!WARNING]
> NeoAstra is under active development and **not ready for public consumption**.

## ✨ Features

- **Your choice of frontend**: plain HTML/JavaScript, React, Vue, or another web framework.
- **Typed .NET ↔ JavaScript RPC** with generated bindings, events, and streaming.
- **Native desktop integration**: windows, menus, dialogs, clipboard, and notifications.
- **Controlled local assets** without a localhost server, with explicit security boundaries.
- **Development tooling**: project templates, frontend builds, and live development workflows.
- **.NET 10 and NativeAOT-friendly** design with source-generated interop and serialization.

## 📖 User guide

See the [documentation](doc/readme.md) for getting started, samples, platform support, security, and building from source.

## 🪪 License

This software is released under the [BSD-2-Clause license](https://opensource.org/licenses/BSD-2-Clause).
Release artifacts also require the applicable [third-party notices](THIRD-PARTY-NOTICES.md).

## 🤗 Author

Alexandre Mutel aka [xoofx](https://xoofx.github.io).

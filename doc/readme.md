# NeoAstra User Guide

Build desktop and mobile apps with .NET and a web UI, on the platform's own webview.

## Start here

- [Getting started](getting-started.md) — packages, a minimal application, and samples.
- [iOS and Android (preview)](application-lifecycle-and-hosting.md#the-simple-host-on-ios-and-android-preview) — the same application on a phone, with the [mobile samples](../samples/mobile/readme.md) to try it.
- [Platforms and runtime dependencies](platform-support.md) — supported targets, browser engines, what each needs installed, and what to do when the native library does not load.
- [Known limitations](known-limitations.md) — what differs between the platforms, and other limits to know about.

## Application development

- [Portable frontend transport](frontend-transport.md) — connect frontend code to the native host through `@neoastra/client`.
- [Typed RPC and generated bindings](rpc-and-bindings.md) — expose strongly typed, NativeAOT-safe backend APIs.
- [Capabilities and security](capabilities-and-security.md) — authorize renderer operations with explicit permissions and scopes.
- [Frontend tooling, production assets, and templates](frontend-tooling-and-assets.md) — configure development, builds, and secure asset hosting.
- [Application lifecycle, launch routing, and hosting](application-lifecycle-and-hosting.md) — manage startup, shutdown, windows, and host integration.
- [Plugins and desktop services](desktop-services.md) — use native features, such as notifications, the clipboard, or the location of a phone, without granting implicit renderer authority.
- [Displays and window placement](displays-and-window-placement.md) — find the displays, put windows on them, follow a dragged tab across windows, and use virtual desktops.
- [Chromeless windows and web title bars](chromeless-windows.md) — extend web content into the title bar while keeping native window behavior, and turn off built-in browser shortcuts and menus.
- [Browser automation](browser-automation.md) — drive the views of an application with the operations of Chrome DevTools MCP, and surface them through an MCP server.
- [Agents and MCP](mcp.md) — let an agent drive the pages of an application in a development session, on the desktop, in a simulator, or on a phone.
- [Delivery and authenticated updates](delivery-and-updates.md) — create deterministic bundles and configure signed updates.

## Security guidance

- [Security threat model](security-threat-model.md) — understand trust boundaries, threats, and required controls.
- [Security and resource-limit review](security-review.md) — review implemented protections and platform-specific limitations.

## Working on NeoAstra

- [Building and verification](building.md) — source prerequisites, native and managed builds, frontend checks, and conformance tools.
- [Open follow-ups](open-follow-ups.md) — known defects and checks that are still to do.

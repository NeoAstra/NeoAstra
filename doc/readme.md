# NeoAstra User Guide

Build native desktop apps with web technologies.

## Start here

- [Getting started](getting-started.md) — packages, a minimal application, and samples.
- [Platforms and runtime dependencies](platform-support.md) — supported targets, browser engines, and what each needs installed.
- [Known limitations](known-limitations.md) — what differs between the platforms, and other limits to know about.

## Application development

- [Portable frontend transport](frontend-transport.md) — connect frontend code to the native host through `@neoastra/client`.
- [Typed RPC and generated bindings](rpc-and-bindings.md) — expose strongly typed, NativeAOT-safe backend APIs.
- [Capabilities and security](capabilities-and-security.md) — authorize renderer operations with explicit permissions and scopes.
- [Frontend tooling, production assets, and templates](frontend-tooling-and-assets.md) — configure development, builds, and secure asset hosting.
- [Application lifecycle, launch routing, and hosting](application-lifecycle-and-hosting.md) — manage startup, shutdown, windows, and host integration.
- [Plugins and desktop services](desktop-services.md) — use native desktop features without granting implicit renderer authority.
- [Chromeless windows and web title bars](chromeless-windows.md) — extend web content into the title bar while keeping native window behavior, and turn off built-in browser shortcuts and menus.
- [Browser automation](browser-automation.md) — drive the views of an application with the operations of Chrome DevTools MCP, and surface them through an MCP server.
- [Delivery and authenticated updates](delivery-and-updates.md) — create deterministic bundles and configure signed updates.

## Security guidance

- [Security threat model](security-threat-model.md) — understand trust boundaries, threats, and required controls.
- [Security and resource-limit review](security-review.md) — review implemented protections and platform-specific limitations.

## Working on NeoAstra

- [Building and verification](building.md) — source prerequisites, native and managed builds, frontend checks, and conformance tools.
- [Open follow-ups](open-follow-ups.md) — known defects and checks that are still to do.

# NeoAstra User Guide

Build native desktop apps with web technologies.

## Start here

- [Platforms and runtime dependencies](platform-support.md) — supported targets, browser backends, and validation status.
- [Known limitations](known-limitations.md) — release-readiness gaps and current platform constraints.

## Application development

- [Portable frontend transport](frontend-transport.md) — connect frontend code to the native host through `@neoastra/client`.
- [Typed RPC and generated bindings](rpc-and-bindings.md) — expose strongly typed, NativeAOT-safe backend APIs.
- [Capabilities and security](capabilities-and-security.md) — authorize renderer operations with explicit permissions and scopes.
- [Frontend tooling, production assets, and templates](frontend-tooling-and-assets.md) — configure development, builds, and secure asset hosting.
- [Application lifecycle, launch routing, and hosting](application-lifecycle-and-hosting.md) — manage startup, shutdown, windows, and host integration.
- [Plugins and desktop services](desktop-services.md) — use native desktop features without granting implicit renderer authority.
- [Delivery and authenticated updates](delivery-and-updates.md) — create deterministic bundles and configure signed updates.

## Security guidance

- [Security threat model](security-threat-model.md) — understand trust boundaries, threats, and required controls.
- [Security and resource-limit review](security-review.md) — review implemented protections and platform-specific limitations.

# NeoAstra v2 Application Platform Specification

> **Historical implementation audit.** NeoAstra is still unreleased. The separate runtime/package names in this document are superseded: the shipping topology is `NeoAstra.Core` plus the full `NeoAstra` package, with RPC, Desktop, Hosting, generator, and build integration consolidated as described in the root README. This file is retained only as design and security review history.

**Status:** Draft 0.1
**Date:** July 26, 2026
**Audience:** NeoAstra maintainers, implementers, plugin authors, tooling authors, and reviewers

## 1. Purpose

NeoAstra v1 establishes a portable system-WebView runtime: a native application loop, owned and embedded windows, WebView2/WKWebView/WebKitGTK backends, profiles, navigation, browser decisions, custom schemes, a guarded JSON message transport, generated native interop, NativeAOT support, and release infrastructure. The normative v1 contract remains in [`neoastra_specs.md`](neoastra_specs.md).

NeoAstra v2 evolves that runtime into a complete, C#-first desktop application platform. It SHALL let an application use any static web frontend, call explicitly exported C# services through generated type-safe bindings, obtain optional native desktop services through narrowly scoped capabilities, and produce normal signed desktop artifacts. NeoAstra SHALL remain frontend-framework-neutral and SHALL continue using the operating system WebView rather than bundling Chromium or Node.js.

This document defines the v2 product architecture, cross-cutting rules, implementation order, and release criteria. The linked sub-specifications define each implementation step in sufficient detail to drive design, coding, tests, and documentation.

## 2. Normative language and scope

The words **MUST**, **MUST NOT**, **REQUIRED**, **SHALL**, **SHALL NOT**, **SHOULD**, **SHOULD NOT**, and **MAY** are normative. A platform-specific requirement applies only when the operating system supports the operation; unsupported behavior MUST be represented through capability discovery or a documented `NotSupported` result, never silent success.

The v2 specification governs new application-platform layers. Unless a v2 document explicitly changes a v1 contract, the v1 ABI, ownership, threading, security, and capability requirements continue to apply.

Terminology:

- **Core:** the `NeoAstra` managed package and its native runtime.
- **View:** a `NeoAstra` browser-view instance. Each view has an immutable application-assigned label.
- **Document session:** the lifetime beginning when a committed document becomes bridge-active and ending on the next navigation, renderer loss, or view disposal.
- **Transport:** authenticated delivery of bounded messages between one document session and managed code.
- **RPC:** the typed command, result, event, channel, and resource protocol above the transport.
- **Permission:** a stable identifier authorizing one command or command group.
- **Scope:** validated data limiting a permission, such as filesystem roots or URL patterns.
- **Capability grant:** an immutable assignment of permissions and scopes to named views, origins, and platforms.
- **Plugin:** a statically registered optional package providing services, permissions, platform adapters, and optionally a frontend module.
- **Tooling:** build-time or development-time software that does not become part of the runtime application unless explicitly published.

## 3. Product promise

The target developer promise is:

> Bring any static web frontend—React, Vue, Svelte, Solid, vanilla TypeScript, or generated HTML. NeoAstra runs it in the system WebView, generates a secure typed client for an explicit C# backend contract, supplies optional native desktop services, and produces a normal signed desktop application. NeoAstra does not own the UI.

A new project SHALL support a path equivalent to:

```text
dotnet new neoastra -n Acme.Notes --frontend react --package-manager pnpm
cd Acme.Notes
dotnet run
dotnet publish -c Release -r win-x64
```

The ordinary development path MUST provide frontend HMR, C# restart through `dotnet watch` or an equivalent documented mechanism, generated TypeScript contracts, secure local production assets, deterministic publish output, and no application-authored WebView bridge glue.

## 4. Goals

NeoAstra v2 MUST provide:

1. A small framework-neutral TypeScript/JavaScript client hiding all backend-specific WebView globals.
2. A versioned, bounded RPC protocol with invocation, structured errors, cancellation, events, ordered channels, and owned resource handles.
3. A C# incremental source generator producing an AOT-safe dispatcher, `System.Text.Json` metadata, TypeScript DTOs, and typed frontend methods.
4. Default-deny command permissions, per-view capability grants, argument scopes, and immutable trusted invocation context.
5. A framework-neutral frontend dev/build contract, Vite-first templates, SPA asset hosting, and secure release defaults.
6. Cancelable close and quit negotiation, activation/reopen/open-file/open-URL delivery, and secure single-instance routing.
7. Optional, statically composed desktop-service plugins for commonly required native features.
8. A thin, inspectable bundle/sign/update workflow using established platform-native tools.
9. Public managed operations for browser capabilities already modeled by the core, plus truthful support reporting.
10. Application-facing mocks, integration automation, diagnostic snapshots, and release security tests.
11. NativeAOT and trimming compatibility for the standard generated path.
12. Explicit platform differences without Chromium-specific behavior being presented as portable.

## 5. Non-goals

NeoAstra v2 MUST NOT:

- implement React/Vue/Svelte components, routing, state management, CSS, layout, a virtual DOM, or a design system;
- expose a Node-like privileged global to arbitrary pages;
- reflect over and export every public C# method, dependency-injection service, or referenced plugin;
- grant renderer access to filesystem, shell, process, network, or credential APIs merely because a package is referenced;
- bundle Chromium or Node.js by default;
- require SSR or a production localhost server for normal applications;
- claim identical WebView behavior across Windows, macOS, and Linux;
- become a general native widget toolkit beyond windows, menus, dialogs, tray surfaces, and operating-system services needed by web-first applications;
- replace established installer, signing, notarization, or package-manager engines with proprietary implementations;
- invent a worker framework where normal .NET async/background services suffice.

## 6. Existing invariants to preserve

Every v2 layer MUST preserve these v1 properties:

- the native ABI remains C-linked, size/versioned, generated into managed interop, and validated at load time;
- UI objects remain owned by their UI thread and release is safe from finalizer/background threads;
- async native completions never run inline before the initiating call returns;
- browser decisions remain timeout-bounded and resolve to documented safe defaults;
- the bridge is disabled unless explicitly enabled;
- source origin is reported only when authenticated by the backend and is never inferred from mutable top-level navigation state;
- Linux WebKitGTK whole-view trust is treated as a material security limitation;
- local assets use secure custom schemes rather than `file://` by default;
- profiles, owned windows, embedded views, raw transport APIs, and backend capability discovery remain usable without RPC, npm, Generic Host, or desktop plugins;
- optional layers do not force reflection, dynamic assembly loading, or runtime code generation.

Breaking changes to these invariants require an explicit specification update, migration guidance, and the appropriate ABI/API/protocol version change.

## 7. Target architecture

```text
Application frontend
  ├── React / Vue / Svelte / Solid / vanilla / other static frontend
  ├── generated application TypeScript API
  └── @neoastra/client + optional @neoastra/plugin-* modules
                         │
                  versioned RPC protocol
                         │
Application backend
  ├── generated RPC dispatcher and serializer context
  ├── explicit application services and authorization
  ├── optional NeoAstra.Hosting / Generic Host integration
  └── statically registered desktop plugins
                         │
NeoAstra core
  ├── transport admission and immutable sender metadata
  ├── app/window/view lifecycle and dispatcher
  ├── browser/profile/custom-scheme operations
  └── native C ABI
                         │
WebView2 / WKWebView / WebKitGTK and native OS services
```

### 7.1 Deliverable boundaries

| Deliverable | Responsibility | Must not do |
| --- | --- | --- |
| `NeoAstra` | Browser/app kernel, transport admission, sender metadata, lifecycle, browser capabilities | Reflect application methods or depend on npm/DI |
| `NeoAstra.Rpc` | Protocol runtime, command registry, dispatch, errors, cancellation, channels/resources, authorization hooks | Expose undeclared services or infer trust |
| `NeoAstra.Rpc.Generator` | Compile-time C# dispatcher, serializers, diagnostics, TypeScript contracts | Require runtime reflection or silently rename wire contracts |
| `@neoastra/client` | Portable frontend transport, invoke/events/channels/resources, diagnostics | Depend on a UI framework or directly expose privileged backend globals |
| `NeoAstra.Hosting` | Optional DI/configuration/logging/background-service integration | Become mandatory for core or RPC use |
| `NeoAstra.Desktop.*` | Optional native desktop service contracts and adapters | Expose frontend commands without grants |
| `NeoAstra.Sdk` | MSBuild integration for frontend build/assets/contracts | Install packages or execute arbitrary network operations implicitly |
| `NeoAstra.Templates` | Vanilla TypeScript and selected SPA scaffolds | Fork or wrap frontend frameworks |
| `NeoAstra.Bundle` | Inspectable bundle/sign/update orchestration | Store signing credentials or hide platform tools |
| `NeoAstra.Testing` | Mocks, deterministic host, automation hooks | Weaken production policy |

Names are part of this draft and MAY be refined before an API freeze. Responsibilities and dependency directions are normative.

### 7.2 Dependency direction

Dependencies MUST point inward:

- frontend plugin modules depend on `@neoastra/client`;
- desktop plugins may depend on `NeoAstra.Rpc` and core, but core MUST NOT depend on plugins;
- `NeoAstra.Hosting` depends on core/RPC, never the reverse;
- tooling may inspect application metadata and generator output but MUST NOT be required at runtime;
- the generator MUST be usable without `NeoAstra.Hosting`;
- application backend APIs MUST remain directly callable from C# even when no frontend command is granted.

## 8. Trust boundaries and identity

### 8.1 Stable identities

Applications MUST assign stable, immutable labels to capability-bearing windows and views. Labels:

- MUST be unique within an application instance;
- MUST be set before bridge/RPC activation;
- MUST NOT derive from a window title, current URL, DOM content, or renderer-provided data;
- SHOULD use predictable logical values such as `main`, `settings`, or `plugin:calendar`;
- MUST be included in diagnostics without exposing user data.

Every inbound invocation MUST be bound to a document-session ID generated by trusted managed/native code. Navigating, renderer replacement, process failure, view disposal, or application shutdown MUST invalidate the session and cancel its calls, subscriptions, channels, and resources.

### 8.2 Authorization layers

Authorization MUST execute in this order:

1. validate transport framing, protocol version, payload limits, and active document session;
2. apply v1 transport admission (`Disabled`, authenticated origins, or explicit whole-view trust);
3. match immutable view capability grants and platform selectors;
4. require the command permission;
5. parse and validate all declared command scopes;
6. execute application-domain authorization inside the command;
7. dispatch the operation.

A failure at any layer MUST prevent command invocation and MUST return a stable, non-sensitive error. Renderer-controlled values MUST NOT be accepted as trusted origin, view label, session ID, permission, or scope.

### 8.3 Secure defaults

Release templates MUST default to controlled local assets, restrictive CSP/security headers, denied remote top-level navigation and popups, disabled DevTools, safe error details, bounded concurrency/resources, and no renderer command grants until explicitly configured. On Linux, a bridge-enabled production view SHOULD contain controlled local content only; remote content belongs in a separate bridge-disabled view or the system browser.

## 9. Compatibility and versioning

NeoAstra has distinct compatibility domains:

| Domain | Versioning rule |
| --- | --- |
| Native ABI | Existing major/minor compatibility rules in v1; incompatible layout/symbol changes require ABI-major change |
| Managed public API | Normal semantic versioning; source/binary breaking changes require a major package version after stable release |
| RPC transport | Explicit protocol major/minor in every handshake; unknown major is rejected, compatible minor features are negotiated |
| Application wire contract | Stable explicit service/method/type names; breaking contract changes require application-controlled versioning |
| Plugin contract | Stable plugin ID plus API/protocol version and declared minimum NeoAstra version |
| Capability files | Versioned schema; unknown security-relevant fields or permission versions fail closed |
| Bundle/update metadata | Versioned schema and signed manifest format; updater never guesses incompatible formats |

Generated output MUST be deterministic. A naming-policy change MUST NOT silently modify an existing explicit wire name. Development mismatch diagnostics SHOULD identify backend/client contract hashes and protocol versions.

## 10. Implementation sequence

The sequence is dependency-ordered. A later step MUST NOT bypass incomplete security or lifecycle foundations from an earlier step.

| Step | Specification | Primary outcome | Depends on |
| ---: | --- | --- | --- |
| 1 | [Portable frontend transport and secure bootstrap](neoastra_v2/01-frontend-transport.md) | One backend-neutral frontend connection and lifecycle contract | v1 transport |
| 2 | [Typed RPC runtime and generated bindings](neoastra_v2/02-rpc-and-bindings.md) | Typed invoke/error/cancel/events/channels/resources under JIT and NativeAOT | Step 1 |
| 3 | [Capabilities, permissions, scopes, and security profiles](neoastra_v2/03-capabilities-and-security.md) | Default-deny, per-view command authority with validated scopes | Steps 1–2 |
| 4 | [Frontend tooling, asset hosting, SDK, and templates](neoastra_v2/04-frontend-tooling-and-assets.md) | One-command dev/HMR and deterministic secure publish | Steps 1–3 |
| 5 | [Application lifecycle, launch routing, and hosting](neoastra_v2/05-application-lifecycle-and-hosting.md) | Cancelable close/quit, activation, early launch events, single instance, Generic Host integration | Core plus Step 3 |
| 6 | [Plugin model and desktop essentials](neoastra_v2/06-plugins-and-desktop-services.md) | Statically composed, capability-gated native desktop services | Steps 3 and 5 |
| 7 | [Bundling, signing, distribution, and updates](neoastra_v2/07-delivery-and-updates.md) | Inspectable signed application artifacts and secure updates | Steps 4–6 |
| 8 | [Browser surface completion, diagnostics, and application testing](neoastra_v2/08-browser-diagnostics-and-testing.md) | Consumable modeled browser features and maintainable application tests | Steps 1–7 as applicable |
| 9 | [Advanced plugins, resources, and isolated work](neoastra_v2/09-advanced-platform-services.md) | Scoped advanced services, binary/resource scalability, utility processes, and ecosystem maturity | Stable Steps 1–8 |

### 10.1 P0 platform milestone

P0 is complete after Steps 1–5 and a reference application prove that a developer can:

- create or clone a React, Vue, or vanilla TypeScript application;
- edit frontend code with HMR;
- invoke a generated typed C# method and receive a typed event;
- cancel an invocation from JavaScript and observe cancellation in C#;
- use different command grants for at least two views;
- navigate/reload without leaking calls, subscriptions, or resources;
- negotiate an asynchronous unsaved-work close/quit;
- publish under NativeAOT with controlled local assets;
- pass release-mode security tests without writing bridge glue.

### 10.2 P1 platform milestone

P1 is complete after Steps 6–8 provide common desktop services, signed platform artifacts, browser API completion, diagnostics, and application automation on all supported operating systems.

### 10.3 P2 platform milestone

P2 is complete when selected Step 9 capabilities have stable plugin contracts, threat models, support matrices, backpressure/resource tests, and real application examples. P2 breadth MUST NOT delay security fixes or truthful P0/P1 platform qualification.

## 11. Cross-cutting implementation rules

### 11.1 API design

- Public C# APIs require XML documentation and specific exceptions.
- Async operations use `Async` suffixes and accept `CancellationToken` where cancellation is meaningful.
- Optional values and platform limitations MUST be explicit.
- Renderer-facing APIs MUST use generated DTOs or narrowly reviewed handwritten contracts.
- Normal C# backend use MUST NOT require a capability grant; grants authorize renderer access, not trusted backend code.
- High-risk operations SHOULD use intent-specific methods rather than arbitrary command strings.

### 11.2 AOT and trimming

The standard path MUST compile under NativeAOT without reflection fallback. Plugins MUST declare source-generated serializers, native assets, and trimming annotations. Static registration is preferred over runtime discovery. Build and CI MUST test at least one generated RPC application under NativeAOT per supported operating system/RID family.

### 11.3 Limits and teardown

All implementations MUST define and test limits for message bytes/depth, concurrent invocations, queued events, channel buffering, resources per session, and shutdown duration. Limits MUST be configurable within safe bounds and visible in diagnostics. Navigation, renderer loss, view disposal, and app shutdown MUST deterministically revoke renderer-owned state.

### 11.4 Platform support

Each public feature MUST report one of the existing support levels with version/details. Documentation MUST distinguish designed, compiled, conformance-tested, runtime-tested, and release-qualified support. A feature unavailable on a platform MUST fail predictably and MUST NOT emulate a weaker security guarantee without explicit documentation.

### 11.5 Accessibility and localization

NeoAstra does not implement web UI accessibility, but native surfaces and templates MUST preserve it. Native menus/dialogs/notifications use OS accessibility; custom title bars retain keyboard/window semantics; templates include language, focus-visible, CSP, and reduced-motion foundations. Theme, high contrast, reduced motion, locale, and locale-change data SHOULD be exposed where reliable. Framework-supplied native strings and installer/update UI MUST be resource-localizable.

## 12. Required quality gates

Every step MUST include:

1. unit tests for state machines, validation, serialization, authorization, and failure paths without requiring a browser where possible;
2. platform adapter contract tests with fakes plus native tests where ABI/platform code changes;
3. browser integration tests for lifecycle and transport behavior;
4. NativeAOT and trimming verification for generated/runtime code;
5. release-mode security tests proving fail-closed behavior;
6. capability-based skips rather than false cross-platform passes;
7. deterministic generated-output checks;
8. public API and configuration documentation;
9. migration notes for changed v1 behavior;
10. package-content, license/notices, and Source Link verification.

Changes are not complete while only a sample succeeds. Tests MUST include invalid input, cancellation races, duplicate/late messages, navigation/disposal races, unavailable platform features, and bounded-resource exhaustion.

## 13. Reference application

A maintained reference application MUST be developed alongside P0/P1. It SHALL use:

- a normal Vite React or Vue frontend with no NeoAstra UI components;
- generated RPC contracts and at least two differently authorized views;
- .NET Generic Host, configuration, DI, logging, and one background service;
- local production assets with restrictive CSP and an explicit external-link policy;
- asynchronous unsaved-work close/quit;
- single-instance launch routing;
- at least one desktop essentials plugin;
- frontend unit tests using the mock transport;
- backend/RPC unit tests and cross-platform integration tests;
- NativeAOT publish and the bundle pipeline.

The reference application is executable specification. It MUST use only documented public paths and MUST fail CI if generated contracts or security configuration drift.

## 14. v2 release acceptance

NeoAstra v2 is release-ready only when:

- all P0 and P1 acceptance criteria are met on Windows x64/ARM64, macOS x64/ARM64, and supported Linux x64/ARM64 environments, with unsupported details documented honestly;
- no sample or application-facing package references raw WebView bridge globals;
- generated RPC works under JIT and NativeAOT without runtime reflection;
- permission, scope, origin, view, session, and teardown denial tests pass;
- lifecycle ordering and single-instance launch delivery pass platform tests;
- desktop essentials have documented support matrices and renderer access is default-denied;
- bundle/install/launch/uninstall smoke tests pass on each claimed artifact type;
- updater signature, downgrade, rollback, interruption, and recovery tests pass before updater support is advertised;
- browser capability operations and events agree with capability reports;
- application diagnostics contain versions/capabilities/security mode without secrets;
- docs, templates, schemas, generated output, package manifests, third-party notices, and migration guidance are current.

## 15. Deferred decisions

These items require prototypes or separate design review before API freeze:

- final NuGet/npm/package names and whether desktop contracts use one package or focused packages;
- exact JSON versus compact/binary transport encoding after the JSON protocol is stable;
- the automation backend chosen per WebView platform;
- the initial installer formats officially supported on each operating system;
- whether utility processes use a NeoAstra-specific typed protocol or an adapter over existing .NET IPC;
- registry/catalog governance for third-party plugins;
- exact application wire-compatibility diagnostics beyond explicit names and contract hashes.

A deferred decision MUST NOT be implemented implicitly in a way that weakens default-deny security, AOT support, or future protocol negotiation.

# Step 9 — Advanced Plugins, Resources, and Isolated Work

**Parent:** [`neoastra_v2_specs.md`](../neoastra_v2_specs.md)
**Depends on:** Stable Steps 1–8
**Outcome:** Advanced applications gain scalable resources, scoped high-risk services, optional process isolation, and a governable plugin ecosystem without enlarging the trusted core indiscriminately.

## 1. Scope and entry gate

Step 9 is not a single “add every Electron API” milestone. Each capability enters through a separate proposal, threat model, support matrix, and plugin contract. No advanced plugin may bypass the Step 3 permission model, Step 5 lifetime model, Step 7 delivery identity, or Step 8 testing/diagnostics.

Candidate areas:

- resource table, binary transfer, and backpressured channels;
- scoped filesystem, HTTP/WebSocket, store/SQL, and process/sidecar plugins;
- network observation/interception, proxy, and advanced browser-session controls;
- deep links/file associations/autostart end-to-end behavior;
- utility process isolation and typed IPC;
- desktop/screen capture, power services, protocol serving, and taskbar/dock integrations;
- crash/support/observability adapters;
- plugin authoring SDK/catalog governance;
- mature automation, accessibility, and localization support.

## 2. Resource table

### 2.1 Model

Long-lived or large backend values SHALL cross RPC as opaque resource IDs owned by one document session:

```csharp
public abstract class NeoRpcResource : IAsyncDisposable
{
    public ulong Id { get; }
}
```

Frontend:

```ts
const resource = await reports.openExport(...);
try {
  for await (const chunk of resource.read()) { ... }
} finally {
  await resource.close();
}
```

A resource entry contains opaque unpredictable/session-namespaced ID, type ID/version, owner application/view/document session, implementation handle, creation/last-use timestamps, byte/count accounting, operation lock policy, and disposal callback.

Rules:

- IDs are never accepted across sessions/views unless an explicit trusted transfer operation exists;
- close is idempotent and terminal;
- navigation/disposal/app shutdown close all descendants in bounded order;
- maximum count/bytes/idle lifetime are enforced per session/view/app/type;
- lookup acquires a safe lease preventing concurrent use-after-dispose;
- resources cannot keep an otherwise closed view/session alive indefinitely;
- plugin unload/update cannot leave live incompatible resources;
- diagnostics expose counts/types/ages, not content.

### 2.2 Resource operations

Commands act on a registered resource type and permission. Arbitrary method names from renderer are not reflected. A resource may expose read/write/seek/query operations only through declared contracts/scopes. Child resources inherit no extra authority.

## 3. Channels and backpressure

Full channel semantics:

- ordered monotonic sequence per channel;
- negotiated window/credit or bounded acknowledgements;
- maximum item bytes and buffered item/byte counts;
- one producer/consumer model initially unless explicitly designed otherwise;
- cancellation/close/completion/error are distinct and idempotent;
- producer awaits backpressure rather than buffering unboundedly;
- stalled consumers time out or are closed by policy;
- navigation/session close cancels producer and drops queued content safely;
- exactly-once delivery is not claimed across renderer/process crash; in-session ordering is guaranteed;
- event loop/UI thread is never blocked waiting for channel credit.

`IAsyncEnumerable<T>` is the natural C# source. The runtime controls enumeration/disposal and contains producer exceptions through structured channel error.

## 4. Binary transfer

Large binary data MUST not use base64 JSON. Approved paths, in priority order:

1. application custom-scheme/resource URL with a session-bound one-time or scoped token;
2. native transport binary frame if all backends can provide bounded equivalent semantics;
3. chunked resource channel with backpressure.

Any token is authorization only when validated by trusted host state and scoped/short-lived; it is not protection against XSS within the same authorized document. Binary APIs validate MIME, length, range behavior, lifetime, and cancellation. Memory pooling/zero-copy optimizations occur only after ownership tests and must not expose freed/reused data.

## 5. Scoped filesystem plugin

Separate permissions: metadata, read file, write/create, enumerate, copy/move, delete, watch, and app-private storage. Scope uses predeclared roots and may issue opaque directory/file capabilities rather than returning ambient paths.

Requirements:

- strict canonicalization and OS-specific path rules;
- no traversal/symlink/reparse/device/alternate-stream escape;
- race-resistant open relative to validated directory handles where OS APIs permit;
- atomic write/replace helpers, explicit overwrite/durability policy;
- bounded streaming and directory enumeration;
- watch overflow/rename semantics and bounded events;
- no unrestricted home/root grant in templates;
- file-picker grants can return a scoped token rather than broad path authority;
- operations remain directly usable in trusted C# without renderer grants.

## 6. HTTP and WebSocket plugins

These plugins exist only when application architecture needs native network access unavailable/undesirable through browser fetch.

HTTP scope constrains scheme, normalized host/port, methods, redirect targets/count, request/response headers, body bytes, response bytes/time, proxy/certificate behavior, and credential policy. DNS rebinding/private-network concerns require resolution-time checks when scopes distinguish network ranges. Sensitive headers and bodies are never logged.

WebSocket scope similarly constrains URL/subprotocols, message size/rate, connection count, idle/total time, and redirects/proxy. Connections are resources; messages use backpressured channels. TLS validation cannot be disabled by a normal permission.

No API accepts arbitrary certificate bypass, proxy credential, or raw socket destination without a separately threat-modeled high-risk permission.

## 7. Store and SQL plugins

### 7.1 Key/value store

Provide named application stores with typed/JSON values, atomic set/delete/batch, optional compare-and-swap, bounded keys/values, flush, and change events. Scope grants named stores/key prefixes and read/write distinctions. Files are app-private and atomic; corruption/recovery/encryption policy is explicit.

### 7.2 SQL

If provided, expose configured database identities and parameterized statements/migrations or a reviewed query model. Do not concatenate renderer input or allow arbitrary connection strings/paths/providers. Scope controls database, read/write/migration permissions and optional statement IDs. Connections/transactions/readers are session/app resources with timeout and row/byte limits. Safe storage may protect credentials/keys, but encryption claims require a reviewed implementation.

## 8. Process, sidecar, and utility process

### 8.1 Scoped process/sidecar

A renderer cannot execute an arbitrary program/shell string. The bundle declares sidecars by stable ID, artifact/hash/RID, allowed argument schema, working-directory policy, environment allowlist, stdio mode, lifetime, instance/concurrency, and sandbox/elevation policy.

Renderer permission selects a declared sidecar and validated arguments. Process output uses bounded channels; input uses bounded writes. Kill/exit/timeout semantics, child-tree cleanup, and app shutdown are explicit. Shell execution is a separate high-risk feature and SHOULD remain backend-only.

### 8.2 Utility process abstraction

Add only for crash isolation, CPU/memory containment, unsafe native libraries, or security separation that in-process .NET cannot provide. Requirements:

- separately published/signed executable tied to application artifact identity;
- authenticated local IPC with versioned generated contracts;
- bounded messages/resources and no ambient renderer connection;
- lifecycle/health/crash/restart/backoff policy;
- sandbox/job/limit primitives where available and honest platform support;
- no assumption that a child process is secure merely because it is separate;
- integration with logging, diagnostics, bundle, update, and shutdown.

Normal `IHostedService` remains preferred for ordinary background work.

## 9. Network observation and interception

Define independent levels:

1. metadata observation;
2. header observation/modification;
3. request cancel/redirect;
4. response observation;
5. response replacement/body streaming.

Capability reports and API contracts MUST distinguish them. Events include immutable request IDs, initiator/frame/origin only when authenticated, method/URI/resource kind, and bounded headers. Body access is opt-in/high-risk and streaming. Decisions have strict deadlines and safe defaults.

Platform implementations must document WebView2 filter/session semantics, WKWebView limitations/public APIs, and WebKitGTK context behavior. No portable claim is made where one engine cannot provide equivalent authority. Custom application-scheme handling remains separate and safer for local assets.

Proxy configuration, cache/storage quota, permission persistence, and advanced session controls require per-profile semantics, restart requirements, credential handling, and capability versions.

## 10. Deep links, associations, and autostart

These features span plugin, bundle metadata, OS registration, single-instance routing, and app launch events. Implementation MUST ensure:

- one source of configuration validated across runtime/bundle;
- URI/file inputs parsed and bounded before app dispatch;
- shell-registration command templates are safely quoted/structured;
- installation/uninstallation/upgrade registration tests;
- early and second-instance delivery exactly once;
- renderer registration changes are not allowed under ordinary permission;
- autostart arguments are fixed/declared, status is queryable, and user/OS policy denial is reported;
- store/sandbox restrictions are explicit.

## 11. Additional platform services

Each should be a focused plugin with capability matrix:

- desktop/screen/window capture with OS permission flow, source enumeration, user consent, stream lifetime, and no silent capture;
- power monitor and prevent-sleep tokens with reasons, ownership, timeout, and cleanup;
- local protocol/server for explicit SSR/sidecar cases, loopback binding, port/auth/lifetime/packaging policy;
- recent documents, badges, jump lists, dock/taskbar actions/progress, and attention integrations;
- media-device details only with permission/privacy controls.

These do not enter the portable core solely for feature-count parity.

## 12. Crash, support, and observability integrations

Provide vendor-neutral hooks for crash dumps, symbol upload metadata, breadcrumbs, logs, traces, and support bundles. Optional adapters may target OpenTelemetry or crash vendors.

Requirements:

- opt-in collection/export;
- redaction/consent and documented data inventory;
- bounded local retention and secure permissions;
- crash-safe minimal native handler behavior;
- symbols tied to exact build/artifact IDs;
- no secrets/command bodies/clipboard/cookies by default;
- support bundle preview and application-added redactors;
- upload retries never block normal app shutdown indefinitely.

## 13. Plugin authoring SDK and governance

The SDK SHALL provide templates, analyzers, test kit, schema generation, AOT checks, support-matrix format, threat-model checklist, and sample plugin. Analyzers detect implicit renderer exposure, missing permissions/scopes, reflection, unbounded DTOs/channels/resources, missing disposal, and absent platform support details where feasible.

A future catalog/registry MUST record:

- owner/source/license/package integrity;
- plugin/API/protocol/permission versions;
- supported NeoAstra/OS/RID matrix;
- native dependencies and AOT status;
- declared commands/permissions/risk level;
- security-review level and date without implying absolute safety;
- conformance results and examples;
- deprecation/advisory information.

NeoAstra MUST NOT auto-load a plugin from catalog metadata or treat popularity as security review. Package manager restore remains explicit.

## 14. Accessibility, localization, and automation maturity

Advanced services SHALL preserve:

- OS accessibility for native surfaces and keyboard semantics;
- high contrast/reduced motion/theme/locale event contracts;
- resource-localized standard labels/installer/update UI;
- Unicode/RTL/path/locale tests;
- documented screen-reader/browser-engine differences;
- remote automation or documented Playwright/WebDriver attachment per backend with production backdoor prevention.

Plugins with user-facing native UI include accessibility/localization acceptance in their support status.

## 15. Per-feature admission process

Before implementation:

1. state user problem and why .NET/application code alone is insufficient;
2. choose core versus official plugin versus community example;
3. define C# API independently of renderer exposure;
4. threat-model renderer path and declare permissions/scopes;
5. define ownership, limits, cancellation, teardown, diagnostics, and platform support;
6. prototype hardest platform and one contrasting platform;
7. review AOT/package/native dependencies;
8. write fake/contract/security/conformance tests and docs;
9. implement all claimed platforms;
10. qualify support honestly and add reference usage.

## 16. Implementation order

Recommended internal order:

- [ ] finish resource table, bounded channels, and binary transfer because other advanced plugins depend on them;
- [ ] implement scoped filesystem and sidecar plugins with strongest path/process security tests;
- [ ] implement HTTP/WebSocket and store plugin; evaluate SQL separately;
- [ ] complete selected browser network/session controls with truthful per-engine levels;
- [ ] integrate deep links/associations/autostart with bundle/single-instance lifecycle;
- [ ] prototype utility process only with a concrete isolation use case;
- [ ] add selected capture/power/platform integrations;
- [ ] add vendor-neutral crash/support/observability hooks;
- [ ] ship plugin SDK/analyzers/test kit and governance metadata;
- [ ] expand accessibility/localization/automation qualification.

## 17. Verification

Cross-cutting tests include resource ownership/use-after-close/ID guessing/session crossing; navigation cleanup; channel order/backpressure/stall/cancel; binary limits; path/symlink/reparse/TOCTOU attacks; URL redirects/DNS/private-network policy; process argument/environment injection and child-tree cleanup; SQL injection/row limits; network decision deadlines; early deep-link/autostart routing; utility crash/restart/update mismatch; plugin AOT/trimming; support-bundle redaction; and capability revocation.

Performance tests track bounded throughput and memory under slow consumers. Native sanitizers and fault injection cover close/cancel races. Every claimed platform has real integration tests or is marked unsupported/experimental.

## 18. Exit criteria

An advanced feature ships only as a versioned, statically composed, AOT-safe contract with default-denied renderer access, scoped authority, deterministic cleanup, bounded resources, correlated diagnostics, threat model, support matrix, mocks, and native/application conformance tests. Step 9 is successful through quality and ecosystem clarity, not the number of APIs added.

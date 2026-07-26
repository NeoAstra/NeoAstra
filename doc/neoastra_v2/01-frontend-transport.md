# Step 1 — Portable Frontend Transport and Secure Bootstrap

**Parent:** [`neoastra_v2_specs.md`](../neoastra_v2_specs.md)
**Outcome:** Frontend code uses one supported API and never branches on WebView2, WKWebView, or WebKitGTK globals.

## 1. Scope

This step standardizes the browser-to-managed transport already supplied by the v1 core. It adds a small injected bootstrap, a framework-neutral npm package, version negotiation, connection/session lifecycle, diagnostics, limits, and a mock transport. It does not dispatch application C# methods; RPC is Step 2.

## 2. Deliverables

1. `@neoastra/client`, distributed as CSP-compatible ESM with TypeScript declarations and an optional CJS compatibility export if justified by tooling tests.
2. A core-managed bootstrap script injected at document start in the main world for bridge-enabled views.
3. Managed transport-session coordination bound to immutable view and document-session identities.
4. A testing export implementing the same client transport contract entirely in memory.
5. Browser fixtures covering WebView2, WKWebView, and WebKitGTK behavior.
6. Migration documentation replacing handwritten bridge selection and custom events.

The package MUST have no dependency on React, Vue, a state library, Node.js at runtime, or a browser polyfill framework. The production module MUST NOT use `eval`, `new Function`, dynamic remote code, or inline script construction that conflicts with a restrictive CSP.

## 3. Layer boundary

The bootstrap is the only code allowed to touch backend-specific browser bridge objects. Application code and generated bindings communicate through a private bootstrap object exposed to `@neoastra/client`.

The private object MUST:

- be installed only for bridge-enabled views;
- expose only bounded send, receive registration, and immutable runtime/session metadata;
- not expose the raw WebView object or arbitrary native functions;
- be non-enumerable and non-writable where the engine permits;
- reject replacement or duplicate initialization;
- verify a private per-document channel binding supplied by trusted host code;
- be invalidated on navigation and renderer replacement.

A random token MUST NOT be described as protection from script already executing in the same document. The security boundary is controlled content plus transport admission and command capabilities, not token secrecy from same-origin scripts.

## 4. Frontend API

The initial public surface SHOULD be equivalent to:

```ts
export interface NeoAstraRuntimeInfo {
  readonly available: true;
  readonly protocolMajor: number;
  readonly protocolMinor: number;
  readonly negotiatedFeatures: readonly string[];
  readonly viewLabel: string;
  readonly documentSessionId: string;
  readonly platform: "windows" | "macos" | "linux";
  readonly backend: "webview2" | "wkwebview" | "webkitgtk";
  readonly wholeViewTrust: boolean;
}

export interface NeoAstraTransportDiagnostic {
  readonly level: "debug" | "information" | "warning" | "error";
  readonly code: string;
  readonly message: string;
  readonly correlationId?: string;
}

export function isAvailable(): boolean;
export function getRuntimeInfo(): NeoAstraRuntimeInfo | undefined;
export function connect(options?: ConnectOptions): Promise<NeoAstraConnection>;
export function onDiagnostic(listener: (value: NeoAstraTransportDiagnostic) => void): () => void;
```

`connect()` MUST be idempotent within one module realm and document session. Concurrent calls share the same handshake. A failed handshake MAY be retried only while the same document session is active and the failure is classified as transient. Importing the package in an ordinary browser MUST be safe: `isAvailable()` returns false and `connect()` rejects with a typed `transport_unavailable` error rather than throwing during module evaluation.

The `NeoAstraConnection` internal/public contract needed by Step 2 MUST provide:

- `send(frame)` with size validation;
- one receive callback registration;
- negotiated feature lookup;
- an `AbortSignal` or equivalent closed notification;
- deterministic `close()`;
- no API for bypassing protocol framing.

## 5. Bootstrap lifecycle

### 5.1 States

Both client and host SHALL implement the following state machine:

```text
Unavailable -> Discovering -> Handshaking -> Connected -> Closing -> Closed
                       \-> Failed -----------^
```

Rules:

- only one active connection exists per document session;
- duplicate `hello` frames return the existing negotiated session or a deterministic duplicate-handshake error;
- no application frame is accepted before handshake completion;
- navigation immediately transitions the old session to `Closing` and then `Closed`;
- late frames for a closed/unknown session are ignored and counted diagnostically, never routed to a new document;
- renderer loss and view disposal close the connection even if JavaScript unload callbacks do not run;
- shutdown is host-authoritative and does not depend on `beforeunload` delivery.

### 5.2 Handshake

The client sends a bounded hello containing only:

```json
{
  "neoastra": 1,
  "kind": "hello",
  "protocol": { "major": 1, "minor": 0 },
  "features": ["invoke", "cancel", "events"],
  "client": { "name": "@neoastra/client", "version": "..." }
}
```

The host response supplies the negotiated major/minor, enabled feature identifiers, immutable view label, opaque document-session ID, platform/backend, whole-view-trust flag, and non-sensitive limits. The client MUST reject a different protocol major. The host MUST choose only features supported by both sides and enabled by policy. Unknown fields are ignored only within a recognized compatible minor version.

The frame discriminator `neoastra` and protocol numbers are reserved. Application messages MUST NOT use transport frames directly.

## 6. Backend adapters

### 6.1 WebView2

- Inject the bootstrap at document start through the existing persistent-script facility.
- Use `window.chrome.webview.postMessage` and its authenticated source metadata path only inside the bootstrap/host adapter.
- Subscribe exactly once per document realm and remove callbacks on teardown where WebView2 permits.
- Preserve JSON values without a stringify/parse mismatch.

### 6.2 WKWebView

- Register one private `WKScriptMessageHandler` name owned by NeoAstra.
- Inject a document-start adapter translating host messages into the common receive path.
- Use `WKScriptMessage` frame/security metadata only when the backend can authenticate it.
- Remove handlers during view destruction and prevent delegate callbacks from reaching released managed state.

### 6.3 WebKitGTK

- Register one private `WebKitUserContentManager` handler and document-start adapter.
- Report source origin as unknown; MUST NOT infer it from `webkit_web_view_get_uri` or message timing.
- Require explicit whole-view trust for any enabled bridge under the supported WebKitGTK API.
- Ensure the bootstrap remains available after normal same-view navigation and is rebound to a new document session.

Backend-specific object names are private implementation details and MUST NOT appear in samples, templates, generated code, or `@neoastra/client` public declarations.

## 7. Framing and limits

Frames MUST be JSON objects with a recognized `kind`, protocol discriminator, and document-session binding maintained by the trusted host adapter. Before parsing nested application data, the host MUST enforce raw byte length. Defaults and hard maxima SHALL be centralized rather than independently configured by JavaScript and managed layers.

Initial configurable limits MUST include:

| Limit | Required behavior |
| --- | --- |
| Maximum frame bytes | Reject before protocol dispatch; no truncation |
| Maximum JSON depth | Reject with `invalid_frame` |
| Maximum handshake attempts | Close abusive session |
| Maximum pre-handshake frames | Zero application frames |
| Maximum diagnostic queue | Drop/coalesce low-severity entries, never grow without bound |
| Handshake timeout | Close session and return typed timeout |

Production diagnostics MUST NOT include frame bodies, command arguments, secrets, file paths, or raw exception text. Development logging MAY include protocol kinds and byte counts.

## 8. Error model

The client package SHALL export `NeoAstraClientError` with stable fields:

```ts
class NeoAstraClientError extends Error {
  readonly code: string;
  readonly correlationId?: string;
  readonly retryable: boolean;
}
```

Step 1 codes include `transport_unavailable`, `handshake_timeout`, `protocol_mismatch`, `connection_closed`, `invalid_frame`, `payload_too_large`, and `internal_transport_error`. Error messages are useful but are not stable machine-readable contracts; `code` is stable.

## 9. Mock transport

`@neoastra/client/testing` MUST:

- install no browser globals by default;
- create an in-memory connection with selectable runtime metadata and negotiated features;
- record outbound frames and inject inbound frames deterministically;
- simulate delays, close, navigation/session replacement, malformed frames, and protocol mismatch;
- support fake time/IDs to avoid flaky tests;
- share frame validation code with the production client where practical;
- never be bundled into a release application unless explicitly imported.

## 10. Implementation order

- [ ] Define transport frame schema, feature identifiers, typed errors, and lifecycle state machine.
- [ ] Add document-session identity and teardown hooks to the managed/core bridge.
- [ ] Implement backend bootstrap adapters and common handshake handling.
- [ ] Implement `@neoastra/client` discovery, connection, diagnostics, and close semantics.
- [ ] Implement the in-memory testing transport.
- [ ] Replace sample/conformance handwritten bridge code.
- [ ] Add package build, types, size budget, license, provenance, and publish checks.
- [ ] Document migration and backend security differences.

## 11. Verification

Required tests:

- ordinary browser import reports unavailable without side effects;
- one and many concurrent `connect()` calls result in one handshake;
- unknown major, compatible minor, unknown feature, malformed JSON, wrong kind, oversized/deep frame, duplicate hello, and timeout cases;
- no pre-handshake application frame reaches managed code;
- navigation invalidates the old session before the new session can connect;
- late old-session messages cannot target a replacement document;
- renderer crash/view disposal/app shutdown close pending clients;
- WebKitGTK reports unknown origin and never synthesizes one;
- CSP fixture succeeds without `unsafe-eval`;
- package works in vanilla TypeScript, Vite React, and Vite Vue builds;
- all sample and harness frontend files are free of raw backend bridge globals.

## 12. Exit criteria

This step is complete when every sample/harness uses `@neoastra/client`, transport tests pass on all three backends, connection teardown survives navigation and renderer loss, package consumers need no framework dependency, and no public frontend path can access backend-specific bridge objects.

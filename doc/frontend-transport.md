# Portable frontend transport

NeoAstra bridge-enabled views use the framework-neutral `@neoastra/client` ESM package. The
managed core injects a document-start bootstrap before returning a new view. Application scripts,
generated bindings, samples, and harnesses must not inspect WebView2, WKWebView, or WebKitGTK
objects. The bootstrap is the sole backend adapter and publishes only a non-enumerable, immutable
transport object discovered privately by the package.

## Migration from handwritten bridge selection

Replace backend selection and custom host-message events:

```js
// Removed: selecting chrome.webview, webkit.messageHandlers, or a custom host event.
```

with the package API:

```ts
import { connect, isAvailable, onDiagnostic } from "@neoastra/client";

if (isAvailable()) {
  const connection = await connect();
  connection.setReceiveHandler(frame => console.log(frame.kind));
  connection.send({ neoastra: 1, kind: "application_frame" });
  onDiagnostic(diagnostic => console.warn(diagnostic.code, diagnostic.message));
}
```

Every bridge-enabled `NeoAstraOptions` now requires an immutable `ViewLabel` unique within the
application. Keep raw `MessageReceived`/`PostMessageAsync` only for v1 compatibility. Once the
package handshake is active, managed transport application frames are unwrapped for
`MessageReceived`, and `PostMessageAsync` wraps a valid object for the active document session.
Generated RPC bindings own those application frame kinds; application code should not
invent transport control kinds (`hello`, `hello_ack`, `close`, or `diagnostic`).

The `NeoAstra` SDK supplies these ESM files. Package-based frontends consume the local package staged
under `obj/neoastra/client`; plain static frontends are materialized with the runtime under
`obj/.../neoastra/frontend` during `dotnet build`. Samples and applications do not keep deployment
copies of `@neoastra/client` in their source trees.

## Lifecycle and limits

`connect()` is idempotent in one module realm and shares concurrent handshakes. The host assigns an
opaque document-session ID only after a compatible hello and committed navigation. Navigation,
renderer loss, view disposal, and application shutdown invalidate the old session. Late old-document
frames are ignored rather than retargeted. A connection exposes an `AbortSignal` through `closed`, a
single receive-handler registration, negotiated feature lookup, bounded `send`, and deterministic
`close()`.

A connection closed by the host reports why through `closeReason`, set before `closed` aborts, and
through one `connection_closed` information diagnostic. `client_close` follows the document's own
`close()`. `navigation`, `view_disposed`, `application_shutdown`, and `renderer_lost` end a document
that is going away. `rpc_session_closed` is different: the host closed the RPC session of a document
that keeps running (see [RPC and bindings](rpc-and-bindings.md)). A close without a valid reason
token reports `unspecified`. A document handshakes once, so `connect()` then rejects with
`connection_closed` and every RPC call fails immediately; a reload is the only way to reconnect, and
whether to reload, and what state to keep, is the application's decision:

```ts
const connection = await connect();
connection.closed.addEventListener("abort", () => {
  // Do not reload on "navigation": that would cancel the navigation already under way.
  if (connection.closeReason === "rpc_session_closed") location.reload();
}, { once: true });
```

`NeoTransportOptions` configures JSON depth, handshake attempts, diagnostic retention, and handshake
timeout. `NeoAstraOptions.MaximumMessageSize` is the raw UTF-8 frame/envelope limit and defaults to
`int.MaxValue` (2 GiB minus one byte), the managed byte-length ceiling, rather than a small payload
quota. Native messaging and the JavaScript client use the same ceiling. Available memory, JSON
serialization and browser-engine string/message limits can be reached sooner; this is not a guarantee
that a 2 GiB message can be allocated or delivered.

`NeoRpcOptions.MaximumFrameBytes` and `MaximumQueuedEventBytesPerSubscription` also default to
`int.MaxValue`. Applications can set smaller positive limits on the view and RPC host to bound memory
use; the strictest applicable limit wins, and view limits include envelope overhead. Event queue count
and overflow policy, concurrency, request rates, timeouts, and JSON nesting-depth limits are unchanged.
For deeply nested DTOs, configure `NeoTransportOptions.MaximumJsonDepth`,
`NeoRpcOptions.MaximumJsonDepth`, and the application's `JsonSerializerContext` options together.
Application frames are never accepted before a handshake. Production
diagnostics contain stable codes and bounded metadata, never frame bodies, arguments, file paths, raw
exceptions, or secrets.

`send` hands a frame to the browser engine at once. It does not wait for the host, and the transport
holds no frame back. On macOS, WKWebView ends the web content process of a page that has 50,000
messages waiting for the host, so a document with that many frames to send should send them in groups
and wait for an answer of the host between two groups; see the paragraph on message bursts under
[backend capability differences](known-limitations.md#backend-capability-differences).

`@neoastra/client/testing` installs no globals. `createMockClient()` supplies deterministic in-memory
connections, selectable metadata/features, outbound recording, inbound injection, fake schedulers and
IDs, protocol mismatch, malformed input, close, and document replacement.

## Backend security differences

| Backend | Bootstrap transport and authenticated metadata | Required trust posture |
| --- | --- | --- |
| WebView2 | Persistent document-start script; WebView2 structured JSON messaging; native sender source metadata | `TrustedOrigins` or explicit whole-view trust |
| WKWebView | One private `WKScriptMessageHandler`; document-start main-world adapter; `WKScriptMessage.frameInfo` origin/main-frame metadata | `TrustedOrigins` or explicit whole-view trust |
| WebKitGTK 6.0 | One private `WebKitUserContentManager` handler; document-start adapter; sender origin remains unknown | Explicit `TrustEntireView`; controlled content only |

The managed host generates `hostViewBinding` and injects it only into the private bootstrap closure. It
admits envelopes to the configured view, but is **not** a secret from script already executing in that
document and is not a sandbox. Each renderer realm generates a `rendererDocumentId`; that value is
untrusted correlation data, not the document's trusted identity. After a compatible hello for the
current navigation, the host creates the separate opaque `documentSessionId` and associates the
renderer value with that host-owned session. Navigation closes the association, and the coordinator
retains closed renderer IDs for the view lifetime so replayed hello and application frames cannot bind
to a replacement document. Security comes from controlled content, native transport admission,
host-owned view/navigation/session state, bounded framing, and command
capabilities. On Linux, never infer sender origin from the current top-level URI or message timing;
remote content belongs in a separate bridge-disabled view.

## Package and CSP checks

The package ships CSP-compatible ESM and declarations only; no CJS export was added because the
TypeScript and Vite consumers all resolve ESM directly. It has no runtime dependencies and does not
use `eval`, `new Function`, dynamic imports, remote code, or backend globals. `npm run check` builds and
tests the package and all backend bootstrap fixtures, enforces the gzip budget, verifies package
contents/license/provenance, scans application frontend files for backend globals, and builds vanilla
TypeScript, Vite React, and Vite Vue fixtures.

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
opaque document-session ID only after a compatible hello. A navigation, renderer loss, view disposal,
and application shutdown invalidate the old session; late old-document frames are ignored rather than
retargeted. A navigation ends the session when it commits a new document. One that never replaces the
document (a download, an answer without content such as 204, `Stop()`, or a navigation that a newer
one supersedes) leaves the document and its session alone, and the document is not told anything. The
hello of the new document is answered as soon as it arrives, while the document still loads; with a
native library that does not report the commit, the host learns of the new document from its hello or
from the end of the navigation, and answers the hello when the navigation ends. Renderer loss is a
`NeoAstra.ProcessFailed` that ends the main document: the view stays usable, and the document that a
reload or a navigation loads next handshakes and gets a new session. A failure that leaves the
document running does not touch its session: with WebView2, the exit of the GPU process or another
helper process, or of a renderer of subframes only (all reported with `RecoveryAction.None`), and a
renderer that stops answering. A connection exposes an `AbortSignal` through `closed`, a single
receive-handler registration, negotiated feature lookup, bounded `send`, and deterministic `close()`.

A connection closed by the host reports why through `closeReason`, set before `closed` aborts, and
through one `connection_closed` information diagnostic. `client_close` follows the document's own
`close()`. `view_disposed` and `application_shutdown` end a document that is going away; a document
that a navigation replaced or whose renderer is lost is gone before anything can reach it, and is told
nothing (the host no longer sends `navigation` or `renderer_lost`). A replaced document that the
engine keeps in its back-forward cache can be shown again, though: its bootstrap reports `navigation`
to it when the document is hidden, so its calls fail at once instead of waiting. `rpc_session_closed` is
different: the host closed the RPC session of a document that keeps running (see
[RPC and bindings](rpc-and-bindings.md)). A close without a valid reason token reports `unspecified`.
A document handshakes once, so `connect()` then rejects with `connection_closed` and every RPC call
fails immediately; a reload is the only way to reconnect, and whether to reload, and what state to
keep, is the application's decision:

```ts
const connection = await connect();
connection.closed.addEventListener("abort", () => {
  // Only a document whose RPC session the host closed keeps running without one.
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
use; the strictest applicable limit wins, and view limits include envelope overhead. An RPC answer,
event, or channel item whose envelope exceeds the view limit is not posted, and only it fails: the
other frames sent with it are posted and the session goes on. The frames
of a document session go only to its own document, while it is the active session of the view: those
that a session wrote before a navigation replaced its document are dropped, never posted to the next
document. Event queue count
and overflow policy, concurrency, request rates, timeouts, and JSON nesting-depth limits are unchanged.
For deeply nested DTOs, configure `NeoTransportOptions.MaximumJsonDepth`,
`NeoRpcOptions.MaximumJsonDepth`, and the application's `JsonSerializerContext` options together.

The client counts JSON depth as `System.Text.Json` counts `MaxDepth` on the host: each object or array
is one level, the frame object included, and a string, number, boolean or null adds none. With the
default depth of 32, a frame may hold 31 nested objects below its own. The host sends the depth it
uses (`NeoTransportOptions.MaximumJsonDepth`) in the handshake. A frame the host sends may use every
level. A frame the page sends may use one level less, because the host reads the envelope around it
with the same limit; `send` throws `invalid_frame` for a deeper frame instead of letting the host drop
it. The walk allocates nothing and stops at the limit.

A frame from the host that the client refuses (deeper than the negotiated depth, or a reserved kind)
is dropped with a warning diagnostic, and never silently: the second argument of
`setReceiveHandler(handler, rejected)` receives its top-level identity (`kind`, `id`, `channel`,
`subscription`, `sequence`) and the error. `NeoRpcClient` uses it to fail what the frame answers with
the same code, for example `invalid_frame`: the pending call rejects, a channel fails and is closed on
the host, a pending subscription rejects and is unsubscribed, and an event is skipped so that the next
events of its subscription keep arriving.

Neither the bootstrap nor the client measures an inbound frame again: the host bounds each envelope it
sends by `MaximumMessageSize` before posting it, and also measures and parses each envelope it receives
on its own. `send` serializes a frame once: the client checks its JSON against the negotiated byte limit
and hands that JSON to the bootstrap with the frame, and the bootstrap adds the fixed size of the
envelope to check the exact envelope bytes, so `send` throws `payload_too_large` rather than having the
host drop the frame. Both encode the JSON in UTF-8 only when its length cannot decide (more than a third
of the limit).
Application frames are never accepted before a handshake. Production
diagnostics contain stable codes and bounded metadata, never frame bodies, arguments, file paths, raw
exceptions, or secrets.

`send` hands a frame to the browser engine at once. It does not wait for the host, and the transport
holds no frame back. On macOS, WKWebView ends the web content process of a page that has 50,000
messages waiting for the host, so a document with that many frames to send should send them in groups
and wait for an answer of the host between two groups; see the paragraph on message bursts under
[backend capability differences](known-limitations.md#backend-capability-differences).

In the other direction, the host posts the frames it queued for a document together, in one turn of
its UI thread, as UTF-8 that the native library reads in place. A native library that reports
`NEOASTRA_BUILD_FEATURE_MESSAGE_BATCH` takes them in one call (`neoastra_view_post_messages`): WebView2
posts them one after the other, each a message of its own as before, while WKWebView and WebKitGTK
dispatch them all, in order, with one evaluated script instead of one script for each frame. A call
holds at most 256 frames and 4 MiB of them (a larger frame goes alone), so that this script stays
bounded. With a native library from before it, the host posts the frames one by one. The page receives
the same frames in the same order either way; on WebKit, the listeners of the frames of one script run
one after the other, before the promise continuations they start, which `NeoRpcClient` does not depend
on: it registers a subscription or a channel when it handles the frame that opens it.

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
renderer value with that host-owned session. A navigation that commits a new document closes the
association, and the coordinator retains closed renderer IDs for the view lifetime so replayed hello
and application frames cannot bind to a replacement document. The frames of the old document are
accepted only until then, while it is still the document of the view. Security comes from controlled content, native transport admission,
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

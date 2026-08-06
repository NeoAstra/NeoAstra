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

`NeoTransportOptions` configures JSON depth, handshake attempts, diagnostic retention, and handshake
timeout. `NeoAstraOptions.MaximumMessageSize` remains the raw UTF-8 frame/envelope limit and cannot
exceed the 16 MiB hard maximum. Application frames are never accepted before a handshake. Production
diagnostics contain stable codes and bounded metadata, never frame bodies, arguments, file paths, raw
exceptions, or secrets.

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

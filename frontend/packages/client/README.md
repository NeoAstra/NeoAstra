# `@neoastra/client`

Framework-neutral, CSP-compatible ESM transport for bridge-enabled NeoAstra views. Importing the
module in an ordinary browser is safe. Use `isAvailable()` for discovery, `connect()` for a single
idempotent per-document handshake, and the explicit `@neoastra/client/testing` export for in-memory
tests. The package has no runtime dependencies and never accesses backend WebView globals.

```ts
import { connect, isAvailable } from "@neoastra/client";

if (isAvailable()) {
  const connection = await connect();
  console.log(connection.runtimeInfo);
}
```

A document handshakes once. When the host closes the connection, `connection.closed` aborts,
`connection.closeReason` names the cause, and RPC calls fail with `connection_closed` instead of
waiting. `rpc_session_closed` means the host closed the RPC session of a document that is still
running; reload the document to reconnect.

## RPC

Generated bindings use the public `invoke` and `subscribe` functions. For direct infrastructure use,
`NeoRpcClient` multiplexes calls, stable `NeoRpcError` values, `AbortSignal` cancellation, ordered event
subscriptions, acknowledged async channels, and session-owned resource close over one connected
transport. An already-aborted call sends no invoke frame. `timeoutMilliseconds` bounds both invocation
completion and the pending `subscribe` acknowledgement; a subscription timeout sends one idempotent
`unsubscribe` and rejects with the stable `timeout` code.
Generated bindings attach their deterministic contract hash; a configured host rejects stale generated
bindings with the stable `protocol_mismatch` code before application dispatch.

`createMockRpcHarness` from `@neoastra/client/testing` registers async mock command handlers, emits
ordered events, propagates cancellation, records outbound protocol frames, and closes outstanding work
without requiring a DOM. Mocks model the RPC contract; they do not claim browser/native conformance.

## Desktop window lifecycle

`createDesktopClient` provides capability-gated current-window conveniences without exposing native
handles. Applications grant `window:management`, `window:close`, and `application:quit` separately:

```ts
import { createDesktopClient, invoke, subscribe } from "@neoastra/client";

const desktop = createDesktopClient({ invoke, subscribe });
const unlisten = await desktop.window.onCloseRequested(async event => {
  if (event.reason === "User") {
    event.preventDefault();
    await desktop.window.hide();
  }
});

await desktop.window.show();
await desktop.window.focus();
await desktop.application.requestQuit();
```

## Web title bars

A window whose host selected the `Overlay` or `Hidden` title-bar style lets the page cover the
title-bar area. `attachTitleBar` publishes the native layout on the document element as
`--neoastra-titlebar-height`, `--neoastra-titlebar-inset-left`, and `--neoastra-titlebar-inset-right`,
together with `data-neoastra-titlebar`, `data-neoastra-window-state`, and
`data-neoastra-window-focused`. Elements marked `data-neoastra-drag-region` move the window; controls
inside them stay interactive.

```ts
const titleBar = await desktop.window.attachTitleBar();
if (titleBar.snapshot.titleBar.rightInset === 0 && titleBar.snapshot.titleBar.leftInset === 0) {
  // No native window controls are shown: render minimize, maximize, and close buttons in the page.
}
await desktop.window.setTitleBar({ style: "Overlay", height: 40 });
```

Close handlers may be asynchronous, but they remain bounded by the host's native close deadline. A
disconnect, timeout, or handler failure preserves a cancelable window. Use the tray activation event to
restore a hidden window; its payload distinguishes primary and secondary activation. Synchronous
pointer-gesture operations and borrowed native handles intentionally remain backend-only.

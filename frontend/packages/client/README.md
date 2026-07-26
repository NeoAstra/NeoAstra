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

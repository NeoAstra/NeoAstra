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

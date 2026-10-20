import assert from "node:assert/strict";
import test from "node:test";

const transportKey = Symbol.for("@neoastra/client/transport/v1");
let sequence = 0;

async function loadClient(bootstrap) {
  if (bootstrap === undefined) delete globalThis[transportKey];
  else Object.defineProperty(globalThis, transportKey, { value: bootstrap, configurable: true });
  return import(`../dist/index.js?test=${++sequence}`);
}

function runtime(features = ["invoke", "cancel", "events"], overrides = {}) {
  return {
    neoastra: 1,
    kind: "hello_ack",
    protocol: { major: 1, minor: 0 },
    features,
    runtime: {
      viewLabel: "main",
      documentSessionId: "document-1",
      platform: "windows",
      backend: "webview2",
      wholeViewTrust: false,
    },
    limits: { maximumFrameBytes: 1024 * 1024, maximumJsonDepth: 32, maximumDiagnosticQueue: 100 },
    ...overrides,
  };
}

function createBootstrap(respond = handler => queueMicrotask(() => handler(runtime()))) {
  let handler;
  const sent = [];
  return {
    sent,
    metadata: {
      platform: "windows",
      backend: "webview2",
      viewLabel: "main",
      wholeViewTrust: false,
      maximumFrameBytes: 1024 * 1024,
      maximumDiagnosticQueue: 100,
      handshakeTimeoutMilliseconds: 10_000,
    },
    send(frame) { sent.push(frame); respond(handler, frame); },
    setReceiveHandler(value) { handler = value; return () => { if (handler === value) handler = undefined; }; },
    receive(frame) { handler?.(frame); },
  };
}

test.afterEach(() => delete globalThis[transportKey]);

// Settles a promise that must not stay pending, or reports that it did.
function outcome(promise, milliseconds = 200) {
  return Promise.race([
    promise.then(value => ({ value }), error => ({ error })),
    new Promise(resolve => setTimeout(() => resolve({ pending: true }), milliseconds)),
  ]);
}

function nest(count, leaf) {
  let value = leaf;
  for (let index = 0; index < count; index++) value = { value };
  return value;
}

test("runtime-sized frame limits accept large payloads in both directions", async () => {
  const maximumFrameBytes = 2 ** 31 - 1;
  const bootstrap = createBootstrap((handler, frame) => {
    if (frame.kind === "hello") queueMicrotask(() => handler(runtime(undefined, {
      limits: { maximumFrameBytes, maximumJsonDepth: 32, maximumDiagnosticQueue: 100 },
    })));
  });
  bootstrap.metadata.maximumFrameBytes = maximumFrameBytes;
  const client = await loadClient(bootstrap);
  const connection = await client.connect();
  const frame = { neoastra: 1, kind: "invoke", value: "x".repeat(17 * 1024 * 1024) };
  connection.send(frame);
  assert.deepEqual(bootstrap.sent.at(-1), frame);
  const received = [];
  connection.setReceiveHandler(value => received.push(value));
  bootstrap.receive(frame);
  assert.deepEqual(received[0], frame);
  connection.close();
});

test("ordinary browser import is side-effect free and unavailable", async () => {
  const client = await loadClient(undefined);
  assert.equal(client.isAvailable(), false);
  assert.equal(client.getRuntimeInfo(), undefined);
  await assert.rejects(client.connect(), error => error.code === "transport_unavailable" && error.retryable === false);
});

test("concurrent connect calls share one hello and one connection", async () => {
  const bootstrap = createBootstrap();
  const client = await loadClient(bootstrap);
  const values = await Promise.all(Array.from({ length: 20 }, () => client.connect()));
  assert.equal(new Set(values).size, 1);
  assert.equal(bootstrap.sent.filter(frame => frame.kind === "hello").length, 1);
  assert.equal(client.getRuntimeInfo().documentSessionId, "document-1");
});

test("a synchronous receive-handler registration failure is normalized and terminal", async () => {
  const bootstrap = createBootstrap();
  let registrations = 0;
  bootstrap.setReceiveHandler = () => {
    registrations++;
    throw new Error("A NeoAstra receive handler is already registered.");
  };
  const client = await loadClient(bootstrap);

  const firstError = await client.connect().catch(error => error);
  assert.ok(firstError instanceof client.NeoAstraClientError);
  assert.equal(firstError.code, "internal_transport_error");
  assert.equal(firstError.retryable, false);
  assert.equal(bootstrap.sent.length, 0);

  const secondError = await client.connect().catch(error => error);
  assert.strictEqual(secondError, firstError);
  assert.equal(registrations, 1);
});

test("protocol mismatch, malformed frame, wrong kind, and timeout are typed", async () => {
  const mismatch = createBootstrap(handler => queueMicrotask(() => handler(runtime([], { protocol: { major: 2, minor: 0 } }))));
  const mismatchClient = await loadClient(mismatch);
  await assert.rejects(mismatchClient.connect(), error => error.code === "protocol_mismatch");
  await assert.rejects(mismatchClient.connect(), error => error.code === "protocol_mismatch");
  assert.equal(mismatch.sent.filter(frame => frame.kind === "hello").length, 1);

  const malformed = createBootstrap(handler => queueMicrotask(() => handler("not-an-object")));
  const malformedClient = await loadClient(malformed);
  await assert.rejects(malformedClient.connect(), error => error.code === "invalid_frame");

  const wrongKind = createBootstrap(handler => queueMicrotask(() => handler({ neoastra: 1, kind: "invoke" })));
  const wrongKindClient = await loadClient(wrongKind);
  await assert.rejects(wrongKindClient.connect(), error => error.code === "invalid_frame");

  const rejected = createBootstrap(handler => queueMicrotask(() => handler({ neoastra: 1, kind: "close", code: "invalid_frame" })));
  const rejectedClient = await loadClient(rejected);
  await assert.rejects(rejectedClient.connect(), error => error.code === "invalid_frame" && !error.retryable);

  const timeout = createBootstrap(() => {});
  const timeoutClient = await loadClient(timeout);
  await assert.rejects(timeoutClient.connect({ handshakeTimeoutMilliseconds: 5 }), error => error.code === "handshake_timeout" && error.retryable);

  let retryAttempts = 0;
  const retry = createBootstrap(handler => {
    retryAttempts++;
    if (retryAttempts === 2) queueMicrotask(() => handler(runtime()));
  });
  const retryClient = await loadClient(retry);
  await assert.rejects(retryClient.connect({ handshakeTimeoutMilliseconds: 5 }), error => error.code === "handshake_timeout" && error.retryable);
  assert.equal((await retryClient.connect()).state, "connected");
});

test("unknown features are ignored and diagnosed", async () => {
  const bootstrap = createBootstrap(handler => queueMicrotask(() => handler(runtime(["invoke", "future"], {
    protocol: { major: 1, minor: 5 },
    ignoredCompatibleMinorField: true,
  }))));
  const client = await loadClient(bootstrap);
  const diagnostics = [];
  client.onDiagnostic(value => diagnostics.push(value));
  const connection = await client.connect();
  assert.deepEqual(connection.runtimeInfo.negotiatedFeatures, ["invoke"]);
  assert.equal(connection.runtimeInfo.protocolMinor, 0);
  assert.equal(diagnostics.at(-1).code, "unknown_feature");
});

test("a host close frame fails the live connection fast and reports its reason", async () => {
  const bootstrap = createBootstrap();
  const client = await loadClient(bootstrap);
  const diagnostics = [];
  client.onDiagnostic(value => diagnostics.push(value));
  const connection = await client.connect();
  const rpc = new client.NeoRpcClient(connection);
  const pending = rpc.invoke("slow", {});

  bootstrap.receive({ neoastra: 1, kind: "close", reason: "rpc_session_closed" });

  await assert.rejects(pending, error => error.code === "connection_closed" && error.retryable === true);
  assert.equal(connection.state, "closed");
  assert.equal(connection.closeReason, "rpc_session_closed");
  assert.equal(connection.closed.aborted, true);
  assert.equal(connection.closed.reason.code, "connection_closed");
  assert.match(connection.closed.reason.message, /rpc_session_closed/);
  assert.deepEqual(
    diagnostics.filter(value => value.code === "connection_closed").map(value => [value.level, value.message]),
    [["information", "The host closed the transport connection (rpc_session_closed)."]]);
  // The document cannot handshake again: later calls fail immediately instead of waiting on the host.
  assert.throws(() => rpc.invoke("later", {}), error => error.code === "connection_closed");
  assert.throws(() => connection.send({ neoastra: 1, kind: "invoke" }), error => error.code === "connection_closed");
  await assert.rejects(client.connect(), error => error.code === "connection_closed");
  assert.equal(bootstrap.sent.filter(frame => frame.kind === "hello").length, 1);
});

test("a host close frame with a malformed reason is reported without echoing it", async () => {
  const bootstrap = createBootstrap();
  const client = await loadClient(bootstrap);
  const diagnostics = [];
  client.onDiagnostic(value => diagnostics.push(value));
  const connection = await client.connect();

  assert.equal(connection.closeReason, undefined);
  let reasonWhenAborted;
  connection.closed.addEventListener("abort", () => { reasonWhenAborted = connection.closeReason; });
  bootstrap.receive({ neoastra: 1, kind: "close", reason: "Not A Token <script>" });

  assert.equal(connection.closed.aborted, true);
  assert.equal(reasonWhenAborted, "unspecified");
  assert.equal(diagnostics.at(-1).message, "The host closed the transport connection (unspecified).");
});

test("connection validates framing, byte size, depth, receive ownership, and close", async () => {
  const bootstrap = createBootstrap();
  const client = await loadClient(bootstrap);
  const connection = await client.connect();
  assert.throws(() => connection.send({ kind: "invoke" }), error => error.code === "invalid_frame");
  assert.throws(() => connection.send({ neoastra: 1, kind: "invoke", value: "x".repeat(1024 * 1024) }), error => error.code === "payload_too_large");
  let deep = {};
  for (let index = 0; index < 40; index++) deep = { deep };
  assert.throws(() => connection.send({ neoastra: 1, kind: "invoke", deep }), error => error.code === "invalid_frame");
  const received = [];
  connection.setReceiveHandler(frame => received.push(frame));
  assert.throws(() => connection.setReceiveHandler(() => {}), error => error.code === "internal_transport_error");
  bootstrap.receive({ neoastra: 1, kind: "hello_ack" });
  assert.equal(received.length, 0);
  connection.close();
  assert.equal(connection.closed.aborted, true);
  assert.equal(connection.closeReason, "client_close");
  assert.throws(() => connection.send({ neoastra: 1, kind: "invoke" }), error => error.code === "connection_closed");
});

test("inbound depth counts nested objects and arrays like the host, not primitive leaves", async () => {
  const bootstrap = createBootstrap();
  const client = await loadClient(bootstrap);
  const connection = await client.connect();
  let next = 0;
  const rpc = new client.NeoRpcClient(connection, { idFactory: () => `depth-${++next}` });

  // The frame object is the first of the 32 negotiated levels: a value of 31 nested objects fills them.
  const objects = rpc.invoke("deep.read", {});
  bootstrap.receive({ neoastra: 1, kind: "result", id: "depth-1", ok: true, value: nest(31, "leaf") });
  assert.deepEqual(await outcome(objects), { value: nest(31, "leaf") });

  let array = [1];
  for (let index = 1; index < 31; index++) array = [array];
  const arrays = rpc.invoke("deep.read", {});
  bootstrap.receive({ neoastra: 1, kind: "result", id: "depth-2", ok: true, value: array });
  assert.deepEqual(await outcome(arrays), { value: array });

  // One more level is refused, and the call it answers fails instead of staying pending.
  const tooDeep = rpc.invoke("deep.read", {});
  bootstrap.receive({ neoastra: 1, kind: "result", id: "depth-3", ok: true, value: nest(32, "leaf") });
  const refused = await outcome(tooDeep);
  assert.equal(refused.error?.code, "invalid_frame");
  assert.equal(refused.error.retryable, false);
  connection.close();
});

test("outbound depth keeps the level of the envelope the host reads with the same limit", async () => {
  const bootstrap = createBootstrap();
  const client = await loadClient(bootstrap);
  const connection = await client.connect();
  // The host parses the envelope around the frame with the negotiated depth: a frame may use 31 levels.
  connection.send({ neoastra: 1, kind: "invoke", value: nest(29, {}) });
  connection.send({ neoastra: 1, kind: "invoke", value: nest(30, "leaf") });
  assert.throws(() => connection.send({ neoastra: 1, kind: "invoke", value: nest(30, {}) }), error => error.code === "invalid_frame");
  assert.throws(() => connection.send({ neoastra: 1, kind: "invoke", value: nest(30, [1]) }), error => error.code === "invalid_frame");
  connection.close();
});

test("a frame the client rejects fails the call, channel or subscription it answers", async () => {
  const bootstrap = createBootstrap();
  const client = await loadClient(bootstrap);
  const diagnostics = [];
  client.onDiagnostic(value => diagnostics.push(value));
  const connection = await client.connect();
  let next = 0;
  const rpc = new client.NeoRpcClient(connection, { idFactory: () => `rejected-${++next}` });
  const deep = nest(40, "leaf");

  // A channel item: the channel fails and the host is asked to close it, rather than waiting for an ack.
  const opening = rpc.invokeChannel("documents.stream", {});
  bootstrap.receive({ neoastra: 1, kind: "result", id: "rejected-1", ok: true, value: { channel: "ch-1" } });
  const iterator = (await outcome(opening)).value[Symbol.asyncIterator]();
  bootstrap.receive({ neoastra: 1, kind: "channel_item", channel: "ch-1", sequence: 1, value: "first" });
  bootstrap.receive({ neoastra: 1, kind: "channel_item", channel: "ch-1", sequence: 2, value: deep });
  assert.deepEqual(await outcome(iterator.next()), { value: { done: false, value: "first" } });
  assert.equal((await outcome(iterator.next())).error?.code, "invalid_frame");
  assert.equal(bootstrap.sent.filter(frame => frame.kind === "channel_close" && frame.channel === "ch-1").length, 1);

  // An event: it is skipped, and the subscription keeps delivering the next ones.
  const values = [];
  const subscribing = rpc.subscribe("documents.changed", value => values.push(value));
  bootstrap.receive({ neoastra: 1, kind: "subscribed", id: "rejected-2" });
  assert.equal(typeof (await outcome(subscribing)).value, "function");
  bootstrap.receive({ neoastra: 1, kind: "event", subscription: "rejected-2", sequence: 1, value: "a" });
  bootstrap.receive({ neoastra: 1, kind: "event", subscription: "rejected-2", sequence: 2, value: deep });
  bootstrap.receive({ neoastra: 1, kind: "event", subscription: "rejected-2", sequence: 3, value: "c" });
  assert.deepEqual(values, ["a", "c"]);

  // A subscription acknowledgement: the subscription fails and the host is asked to drop it.
  const failing = rpc.subscribe("documents.other", () => {});
  bootstrap.receive({ neoastra: 1, kind: "subscribed", id: "rejected-3", error: deep });
  assert.equal((await outcome(failing)).error?.code, "invalid_frame");
  assert.equal(bootstrap.sent.filter(frame => frame.kind === "unsubscribe" && frame.id === "rejected-3").length, 1);

  assert.equal(diagnostics.filter(value => value.code === "invalid_frame" && /depth/.test(value.message)).length, 3);
  connection.close();
});

test("rpcClient retries the handshake after a retryable failure", async () => {
  let hellos = 0;
  const bootstrap = createBootstrap((handler, frame) => {
    if (frame.kind === "hello" && ++hellos === 2) queueMicrotask(() => handler(runtime()));
    if (frame.kind === "invoke") queueMicrotask(() => handler({ neoastra: 1, kind: "result", id: frame.id, ok: true, value: "read" }));
  });
  bootstrap.metadata.handshakeTimeoutMilliseconds = 5;
  const client = await loadClient(bootstrap);
  await assert.rejects(client.invoke("documents.read", {}), error => error.code === "handshake_timeout" && error.retryable);
  assert.equal((await outcome(client.invoke("documents.read", {}))).value, "read");
  assert.equal(hellos, 2);
  // The bound on attempts is tested in rpc.test.mjs: rpcClient keeps one module-level client per test process.
});

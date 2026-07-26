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
  assert.throws(() => connection.send({ neoastra: 1, kind: "invoke" }), error => error.code === "connection_closed");
});

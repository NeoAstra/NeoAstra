import assert from "node:assert/strict";
import test from "node:test";
import { createMockClient } from "../dist/testing.js";

test("mock transport records frames and injects inbound frames deterministically", async () => {
  const mock = createMockClient({ idFactory: () => "fixed-id", negotiatedFeatures: ["invoke"] });
  const [first, second] = await Promise.all([mock.connect(), mock.connect()]);
  assert.equal(first, second);
  assert.equal(mock.outboundFrames.filter(frame => frame.kind === "hello").length, 1);
  assert.equal(first.runtimeInfo.documentSessionId, "fixed-id");
  const inbound = [];
  first.setReceiveHandler(frame => inbound.push(frame));
  first.send({ neoastra: 1, kind: "invoke", id: "1" });
  mock.injectInbound({ neoastra: 1, kind: "result", id: "1" });
  assert.equal(mock.outboundFrames.at(-1).kind, "invoke");
  assert.equal(inbound.at(-1).kind, "result");
});

test("mock supports delay, protocol mismatch, malformed input, close, and navigation", async () => {
  const callbacks = [];
  const scheduler = {
    setTimeout(callback) { callbacks.push(callback); return callback; },
    clearTimeout() {},
  };
  const delayed = createMockClient({ scheduler, connectDelayMilliseconds: 100 });
  let completed = false;
  const pending = delayed.connect().then(() => { completed = true; });
  await Promise.resolve();
  assert.equal(completed, false);
  callbacks.shift()();
  await pending;

  const timingOut = createMockClient({ scheduler, connectDelayMilliseconds: 100 });
  const timedOut = timingOut.connect({ handshakeTimeoutMilliseconds: 50 });
  callbacks.shift()();
  await assert.rejects(timedOut, error => error.code === "handshake_timeout" && error.retryable);

  const mismatch = createMockClient({ protocolMajor: 2 });
  await assert.rejects(mismatch.connect(), error => error.code === "protocol_mismatch");

  const diagnostics = [];
  delayed.onDiagnostic(value => diagnostics.push(value));
  delayed.injectMalformed();
  assert.equal(diagnostics.at(-1).code, "invalid_frame");
  const oldConnection = await delayed.connect();
  const replacement = delayed.navigate({ idFactory: () => "replacement-id" });
  assert.equal(oldConnection.closed.aborted, true);
  const replacementPending = replacement.connect();
  callbacks.shift()();
  assert.equal((await replacementPending).runtimeInfo.documentSessionId, "replacement-id");
});

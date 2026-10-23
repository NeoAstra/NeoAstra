import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import path from "node:path";
import test from "node:test";
import vm from "node:vm";
import { webcrypto } from "node:crypto";

const templatePath = path.resolve(import.meta.dirname, "../../../../src/NeoAstra.Core/Transport/transport-bootstrap.js");
const template = await readFile(templatePath, "utf8");

function scriptFor(backend, platform = "windows", maximumFrameBytes = 1048576) {
  return template
    .replaceAll("__NEOASTRA_HOST_VIEW_BINDING__", "host-binding")
    .replaceAll("__NEOASTRA_MAXIMUM_FRAME_BYTES__", String(maximumFrameBytes))
    .replaceAll("__NEOASTRA_MAXIMUM_DIAGNOSTIC_QUEUE__", "100")
    .replaceAll("__NEOASTRA_HANDSHAKE_TIMEOUT_MILLISECONDS__", "10000")
    .replaceAll("__NEOASTRA_PLATFORM__", platform)
    .replaceAll("__NEOASTRA_BACKEND__", backend)
    .replaceAll("__NEOASTRA_VIEW_LABEL__", "fixture")
    .replaceAll("__NEOASTRA_WHOLE_VIEW_TRUST__", platform === "linux" ? "true" : "false");
}

function runBackend(backend, platform, maximumFrameBytes, relayed = false) {
  const outbound = [];
  const listeners = new Map();
  const context = {
    crypto: webcrypto,
    TextEncoder,
    Symbol,
    Object,
    Array,
    Error,
    TypeError,
    addEventListener(name, listener) { listeners.set(name, listener); },
  };
  context.globalThis = context;
  context.top = context;
  if (backend === "webview2") {
    context.chrome = { webview: {
      postMessage(value) { outbound.push(value); },
      addEventListener(name, listener) { listeners.set(`webview:${name}`, listener); },
    } };
  } else if (relayed) {
    // The handler is in a script world of its own: the window dispatches the envelopes as JSON text for its relay.
    context.webkit = { messageHandlers: {} };
    context.CustomEvent = class { constructor(type, init) { this.type = type; this.detail = init.detail; } };
    context.EventTarget = { prototype: { dispatchEvent(event) {
      assert.equal(this.webkit, context.webkit);
      assert.equal(event.type, "neoastra-transport-v1-post");
      outbound.push(JSON.parse(event.detail));
      return true;
    } } };
  } else {
    context.webkit = { messageHandlers: { _neoastra_transport_v1: { postMessage(value) { outbound.push(value); } } } };
  }
  vm.runInNewContext(scriptFor(backend, platform, maximumFrameBytes), context, { filename: `bootstrap-${backend}.js` });
  const transport = context[Symbol.for("@neoastra/client/transport/v1")];
  return { context, transport, outbound, listeners };
}

for (const [backend, platform] of [["webview2", "windows"], ["wkwebview", "macos"], ["webkitgtk", "linux"]]) {
  test(`bootstrap adapts ${backend} without exposing its raw object`, () => {
    const fixture = runBackend(backend, platform);
    assert.equal(fixture.transport.metadata.backend, backend);
    assert.equal(Object.isFrozen(fixture.transport), true);
    assert.equal(Object.getOwnPropertyDescriptor(fixture.context, Symbol.for("@neoastra/client/transport/v1")).enumerable, false);
    fixture.transport.send({ neoastra: 1, kind: "hello" });
    assert.equal(fixture.outbound.length, 1);
    assert.equal(fixture.outbound[0].hostViewBinding, "host-binding");
    assert.match(fixture.outbound[0].rendererDocumentId, /^[0-9a-f]{32}$/);
    assert.notEqual(fixture.outbound[0].rendererDocumentId, fixture.outbound[0].hostViewBinding);
    assert.equal("chrome" in fixture.transport, false);
    assert.equal("webkit" in fixture.transport, false);
  });
}

test("bootstrap relays its envelopes as JSON text when WebKitGTK keeps the handler out of the main world", () => {
  const fixture = runBackend("webkitgtk", "linux", 1048576, true);
  fixture.transport.send({ neoastra: 1, kind: "hello", text: "\"quoted\" é" });
  fixture.transport.send({ neoastra: 1, kind: "invoke" }, JSON.stringify({ neoastra: 1, kind: "invoke" }));
  assert.equal(fixture.outbound.length, 2);
  assert.equal(fixture.outbound[0].__neoastraTransport, 1);
  assert.equal(fixture.outbound[0].hostViewBinding, "host-binding");
  assert.match(fixture.outbound[0].rendererDocumentId, /^[0-9a-f]{32}$/);
  assert.deepEqual(fixture.outbound[0].frame, { neoastra: 1, kind: "hello", text: "\"quoted\" é" });
  assert.deepEqual(fixture.outbound[1].frame, { neoastra: 1, kind: "invoke" });
  assert.equal(typeof fixture.listeners.get("neoastramessage"), "function");
});

test("bootstrap leaves the transport out on WebKit without a handler or a relay", () => {
  const fixture = runBackend("wkwebview", "macos", 1048576, true);
  assert.equal(fixture.transport, undefined);
});

test("bootstrap accepts only its host view binding and renderer document correlation", () => {
  const fixture = runBackend("webview2", "windows");
  const received = [];
  fixture.transport.setReceiveHandler(value => received.push(value));
  fixture.transport.send({ neoastra: 1, kind: "hello" });
  const envelope = fixture.outbound[0];
  fixture.listeners.get("webview:message")({ data: { ...envelope, hostViewBinding: "wrong", frame: { neoastra: 1, kind: "close" } } });
  fixture.listeners.get("webview:message")({ data: { ...envelope, frame: { neoastra: 1, kind: "hello_ack" } } });
  fixture.listeners.get("webview:message")({ data: { ...envelope, rendererDocumentId: "wrong", frame: { neoastra: 1, kind: "close" } } });
  assert.deepEqual(received, [{ neoastra: 1, kind: "hello_ack" }]);
});

test("bootstrap reports envelope byte-limit failures with a stable code", () => {
  const fixture = runBackend("webview2", "windows", 128);
  assert.throws(
    () => fixture.transport.send({ neoastra: 1, kind: "hello", padding: "x".repeat(128) }),
    error => error.code === "payload_too_large");
});

test("bootstrap delivers inbound frames without measuring them again, since the host bounded them", () => {
  const fixture = runBackend("webview2", "windows", 256);
  const received = [];
  fixture.transport.setReceiveHandler(value => received.push(value));
  fixture.transport.send({ neoastra: 1, kind: "hello" });
  const envelope = fixture.outbound[0];
  // A frame over the bootstrap's own count still reaches the client, which fails what it answers if it must.
  fixture.listeners.get("webview:message")({ data: { ...envelope, frame: { neoastra: 1, kind: "result", id: "req-1", ok: true, value: "x".repeat(512) } } });
  assert.equal(received.length, 1);
  assert.equal(received[0].id, "req-1");
});

test("bootstrap closes the connection of a document kept in the back-forward cache", () => {
  for (const [backend, platform] of [["webview2", "windows"], ["wkwebview", "macos"], ["webkitgtk", "linux"]]) {
    const fixture = runBackend(backend, platform);
    const received = [];
    fixture.transport.setReceiveHandler(value => received.push(value));
    // A document unloaded for good is not told anything: it is gone.
    fixture.listeners.get("pagehide")({ persisted: false });
    assert.deepEqual(received, []);
    // A cached one was replaced, and the host ended its session without a frame that would reach the new document.
    fixture.listeners.get("pagehide")({ persisted: true });
    // The frame is an object of the bootstrap realm.
    assert.deepEqual(JSON.parse(JSON.stringify(received)), [{ neoastra: 1, kind: "close", reason: "navigation" }]);
  }
});

test("bootstrap source is compatible with restrictive script CSP", () => {
  assert.doesNotMatch(template, /\beval\s*\(|\bnew\s+Function\b|import\s*\(/);
  assert.doesNotMatch(template, /https?:\/\//);
});

test("bootstrap counts the exact envelope bytes on send, from the frame JSON the client already made", () => {
  for (const padding of ["x", "é"]) {
    const frameFor = length => ({ neoastra: 1, kind: "invoke", padding: padding.repeat(length) });
    const probe = runBackend("webview2", "windows", 2 ** 31 - 1);
    probe.transport.send(frameFor(0));
    const envelopeBytes = length => new TextEncoder().encode(JSON.stringify({ ...probe.outbound[0], frame: frameFor(length) })).byteLength;
    const fixture = runBackend("webview2", "windows", envelopeBytes(40));
    const fits = frameFor(40);
    const over = frameFor(41);
    fixture.transport.send(fits);
    fixture.transport.send(fits, JSON.stringify(fits));
    assert.equal(fixture.outbound.length, 2, padding);
    assert.throws(() => fixture.transport.send(over), error => error.code === "payload_too_large", padding);
    assert.throws(() => fixture.transport.send(over, JSON.stringify(over)), error => error.code === "payload_too_large", padding);
  }
  // The JSON given by the client is used as is: the bootstrap does not serialize the frame again.
  const fixture = runBackend("webview2", "windows", 1024);
  fixture.transport.send({ neoastra: 1, kind: "invoke", value: { toJSON() { throw new Error("serialized again"); } } }, "{\"neoastra\":1,\"kind\":\"invoke\"}");
  assert.equal(fixture.outbound.length, 1);
});

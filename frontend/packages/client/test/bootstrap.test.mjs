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

function runBackend(backend, platform, maximumFrameBytes) {
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

test("bootstrap source is compatible with restrictive script CSP", () => {
  assert.doesNotMatch(template, /\beval\s*\(|\bnew\s+Function\b|import\s*\(/);
  assert.doesNotMatch(template, /https?:\/\//);
});

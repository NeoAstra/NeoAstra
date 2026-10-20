// Measures the page side of the transport for a large frame in each direction: the real bootstrap
// (src/NeoAstra.Core/Transport/transport-bootstrap.js) and the built @neoastra/client, without a browser.
// Run with `npm run bench` after `npm run build`. Numbers are medians in milliseconds.
import { readFile } from "node:fs/promises";
import path from "node:path";
import { performance } from "node:perf_hooks";
import vm from "node:vm";

const iterations = Number(process.env.BENCH_ITERATIONS ?? 200);
const itemCount = Number(process.env.BENCH_ITEMS ?? 10_000);
const template = await readFile(path.resolve(import.meta.dirname, "../../src/NeoAstra.Core/Transport/transport-bootstrap.js"), "utf8");
const maximumFrameBytes = 2 ** 31 - 1;
const script = template
  .replaceAll("__NEOASTRA_HOST_VIEW_BINDING__", "0123456789abcdef0123456789abcdef")
  .replaceAll("__NEOASTRA_MAXIMUM_FRAME_BYTES__", String(maximumFrameBytes))
  .replaceAll("__NEOASTRA_MAXIMUM_DIAGNOSTIC_QUEUE__", "100")
  .replaceAll("__NEOASTRA_HANDSHAKE_TIMEOUT_MILLISECONDS__", "10000")
  .replaceAll("__NEOASTRA_PLATFORM__", "windows")
  .replaceAll("__NEOASTRA_BACKEND__", "webview2")
  .replaceAll("__NEOASTRA_VIEW_LABEL__", "bench")
  .replaceAll("__NEOASTRA_WHOLE_VIEW_TRUST__", "false");

const listeners = [];
let lastPosted;
globalThis.top = globalThis;
globalThis.chrome = { webview: {
  postMessage(value) { lastPosted = value; },
  addEventListener(name, listener) { if (name === "message") listeners.push(listener); },
} };
vm.runInThisContext(script, { filename: "transport-bootstrap.js" });
const client = await import("../packages/client/dist/index.js");

const deliver = frame => {
  const envelope = { __neoastraTransport: 1, hostViewBinding: "0123456789abcdef0123456789abcdef", rendererDocumentId: lastPosted.rendererDocumentId, frame };
  for (const listener of listeners) listener({ data: envelope });
};
const connecting = client.connect();
deliver({
  neoastra: 1,
  kind: "hello_ack",
  protocol: { major: 1, minor: 0 },
  features: ["invoke", "cancel", "events"],
  runtime: { viewLabel: "bench", documentSessionId: "document-1", platform: "windows", backend: "webview2", wholeViewTrust: false },
  limits: { maximumFrameBytes, maximumJsonDepth: 32, maximumDiagnosticQueue: 100 },
});
const connection = await connecting;
let received = 0;
connection.setReceiveHandler(() => { received++; });

const items = Array.from({ length: itemCount }, (_, index) => ({
  id: index,
  name: `document-${index}.md`,
  path: `C:/Users/someone/projects/sample/docs/document-${index}.md`,
  size: index * 37,
  modified: "2026-10-20T10:00:00.000Z",
  tags: ["draft", index % 2 === 0 ? "even" : "odd"],
}));
const result = { neoastra: 1, kind: "result", id: "req-1", ok: true, value: { items } };
const resultJson = JSON.stringify({ __neoastraTransport: 1, hostViewBinding: "0123456789abcdef0123456789abcdef", rendererDocumentId: lastPosted.rendererDocumentId, frame: result });
const invoke = { neoastra: 1, kind: "invoke", id: "req-2", command: "documents.save", args: { items } };

function median(action) {
  for (let index = 0; index < 20; index++) action();
  const samples = [];
  for (let index = 0; index < iterations; index++) {
    const start = performance.now();
    action();
    samples.push(performance.now() - start);
  }
  samples.sort((left, right) => left - right);
  return samples[samples.length >> 1];
}

const parse = median(() => JSON.parse(resultJson));
const inbound = median(() => deliver(result));
const outbound = median(() => connection.send(invoke));
if (received === 0) throw new Error("The benchmark frame never reached the receive handler.");
console.log(`frame: ${itemCount} objects, ${(Buffer.byteLength(resultJson) / 1024).toFixed(0)} KiB envelope, ${iterations} iterations`);
console.log(`JSON.parse of the envelope (reference, done by the browser): ${parse.toFixed(3)} ms`);
console.log(`inbound bootstrap + client checks:                        ${inbound.toFixed(3)} ms`);
console.log(`outbound client + bootstrap checks:                       ${outbound.toFixed(3)} ms`);
connection.close();

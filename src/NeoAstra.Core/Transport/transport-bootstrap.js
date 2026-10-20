(() => {
  "use strict";
  if (globalThis !== globalThis.top) return;

  const key = Symbol.for("@neoastra/client/transport/v1");
  if (Object.prototype.hasOwnProperty.call(globalThis, key)) {
    throw new Error("NeoAstra transport bootstrap was initialized more than once.");
  }

  const hostViewBinding = "__NEOASTRA_HOST_VIEW_BINDING__";
  const maximumFrameBytes = __NEOASTRA_MAXIMUM_FRAME_BYTES__;
  const maximumDiagnosticQueue = __NEOASTRA_MAXIMUM_DIAGNOSTIC_QUEUE__;
  const handshakeTimeoutMilliseconds = __NEOASTRA_HANDSHAKE_TIMEOUT_MILLISECONDS__;
  const metadata = Object.freeze({
    platform: "__NEOASTRA_PLATFORM__",
    backend: "__NEOASTRA_BACKEND__",
    viewLabel: "__NEOASTRA_VIEW_LABEL__",
    wholeViewTrust: __NEOASTRA_WHOLE_VIEW_TRUST__,
    maximumFrameBytes,
    maximumDiagnosticQueue,
    handshakeTimeoutMilliseconds,
  });
  const random = new Uint8Array(16);
  globalThis.crypto.getRandomValues(random);
  const rendererDocumentId = Array.from(random, value => value.toString(16).padStart(2, "0")).join("");
  let receiveHandler;

  const transportError = (code, message) => {
    const error = new Error(message);
    Object.defineProperty(error, "code", { value: code, enumerable: true });
    return error;
  };

  const encoder = new TextEncoder();
  // A UTF-16 code unit takes one to three UTF-8 bytes: the string is encoded only when its length cannot decide.
  const exceedsBytes = (json, limit) => json.length * 3 > limit && (json.length > limit || encoder.encode(json).byteLength > limit);
  // The envelope adds the same bytes around every frame: they are measured once.
  const envelopeBytes = encoder.encode(JSON.stringify({ __neoastraTransport: 1, hostViewBinding, rendererDocumentId, frame: null })).byteLength - "null".length;

  // The host bounds every envelope it sends by the same limit before posting it: measuring it again here would
  // only cost a serialization, and a throw from this listener would drop an answer the client is waiting for.
  const receiveEnvelope = value => {
    if (!value || typeof value !== "object" || value.__neoastraTransport !== 1 ||
        value.hostViewBinding !== hostViewBinding || value.rendererDocumentId !== rendererDocumentId) return;
    if (!value.frame || typeof value.frame !== "object" || Array.isArray(value.frame)) return;
    receiveHandler?.(value.frame);
  };

  let postEnvelope;
  if (metadata.backend === "webview2") {
    const adapter = globalThis.chrome?.webview;
    if (!adapter || typeof adapter.postMessage !== "function" || typeof adapter.addEventListener !== "function") return;
    adapter.addEventListener("message", event => receiveEnvelope(event.data));
    postEnvelope = value => adapter.postMessage(value);
  } else {
    const adapter = globalThis.webkit?.messageHandlers?._neoastra_transport_v1;
    if (!adapter || typeof adapter.postMessage !== "function") return;
    globalThis.addEventListener("neoastramessage", event => receiveEnvelope(event.detail));
    postEnvelope = value => adapter.postMessage(value);
  }

  // A document that the engine keeps in its back-forward cache is hidden, not unloaded, once the next document has
  // replaced it: the host ended its session then without a word to it, since a frame would reach the new document.
  // Its client learns it here, and does not wait on calls that nobody answers if the document is shown again.
  globalThis.addEventListener("pagehide", event => {
    if (event.persisted) receiveHandler?.({ neoastra: 1, kind: "close", reason: "navigation" });
  });

  const transport = Object.freeze({
    metadata,
    // `json`, when given, is the frame as the client already serialized it. It only spares a second
    // serialization: the host measures and parses what it receives on its own.
    send(frame, json) {
      if (!frame || typeof frame !== "object" || Array.isArray(frame)) throw new Error("NeoAstra frames must be JSON objects.");
      if (typeof json !== "string") {
        try { json = JSON.stringify(frame); }
        catch { throw transportError("invalid_frame", "NeoAstra frames must be JSON serializable."); }
        if (typeof json !== "string") throw transportError("invalid_frame", "NeoAstra frames must be JSON values.");
      }
      if (exceedsBytes(json, maximumFrameBytes - envelopeBytes)) throw transportError("payload_too_large", "NeoAstra frame exceeds the configured byte limit.");
      postEnvelope({
        __neoastraTransport: 1,
        hostViewBinding,
        rendererDocumentId,
        frame,
      });
    },
    setReceiveHandler(handler) {
      if (typeof handler !== "function") throw new TypeError("A receive handler is required.");
      if (receiveHandler !== undefined) throw new Error("A NeoAstra receive handler is already registered.");
      receiveHandler = handler;
      return () => { if (receiveHandler === handler) receiveHandler = undefined; };
    },
  });

  Object.defineProperty(globalThis, key, {
    value: transport,
    enumerable: false,
    configurable: false,
    writable: false,
  });
})();

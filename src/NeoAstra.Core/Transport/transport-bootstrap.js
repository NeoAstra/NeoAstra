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

  const byteLength = value => {
    let json;
    try { json = JSON.stringify(value); }
    catch { throw transportError("invalid_frame", "NeoAstra frames must be JSON serializable."); }
    if (json === undefined) throw transportError("invalid_frame", "NeoAstra frames must be JSON values.");
    const length = new TextEncoder().encode(json).byteLength;
    if (length > maximumFrameBytes) throw transportError("payload_too_large", "NeoAstra frame exceeds the configured byte limit.");
    return length;
  };

  const receiveEnvelope = value => {
    if (!value || typeof value !== "object" || value.__neoastraTransport !== 1 ||
        value.hostViewBinding !== hostViewBinding || value.rendererDocumentId !== rendererDocumentId) return;
    byteLength(value);
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

  const transport = Object.freeze({
    metadata,
    send(frame) {
      if (!frame || typeof frame !== "object" || Array.isArray(frame)) throw new Error("NeoAstra frames must be JSON objects.");
      const envelope = {
        __neoastraTransport: 1,
        hostViewBinding,
        rendererDocumentId,
        frame,
      };
      byteLength(envelope);
      postEnvelope(envelope);
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

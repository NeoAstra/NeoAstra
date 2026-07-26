import { DEFAULT_DIAGNOSTIC_QUEUE_LIMIT, HARD_MAXIMUM_FRAME_BYTES, NeoAstraClientError, PROTOCOL_MAJOR, PROTOCOL_MINOR, SUPPORTED_FEATURES, assertApplicationFrame, isRecord, } from "./shared.js";
export { NeoAstraClientError } from "./shared.js";
const transportKey = Symbol.for("@neoastra/client/transport/v1");
const transportErrorCodes = Object.freeze([
    "transport_unavailable",
    "handshake_timeout",
    "protocol_mismatch",
    "connection_closed",
    "invalid_frame",
    "payload_too_large",
    "internal_transport_error",
]);
let state = "unavailable";
let connection;
let handshake;
let terminalHandshakeError;
let runtimeInfo;
const diagnosticListeners = new Set();
const diagnosticQueue = [];
let diagnosticQueueLimit = DEFAULT_DIAGNOSTIC_QUEUE_LIMIT;
function discover() {
    const candidate = globalThis[transportKey];
    if (!isRecord(candidate) || !isRecord(candidate.metadata) || typeof candidate.send !== "function" || typeof candidate.setReceiveHandler !== "function") {
        return undefined;
    }
    return candidate;
}
export function isAvailable() {
    return discover() !== undefined;
}
export function getRuntimeInfo() {
    return runtimeInfo;
}
export function onDiagnostic(listener) {
    if (typeof listener !== "function")
        throw new TypeError("A diagnostic listener is required.");
    diagnosticListeners.add(listener);
    for (const value of diagnosticQueue) {
        try {
            listener(value);
        }
        catch { }
    }
    return () => diagnosticListeners.delete(listener);
}
export function connect(options = {}) {
    if (connection?.state === "connected")
        return Promise.resolve(connection);
    if (connection !== undefined)
        return Promise.reject(new NeoAstraClientError("connection_closed", "The transport connection is closed."));
    if (terminalHandshakeError !== undefined)
        return Promise.reject(terminalHandshakeError);
    if (handshake !== undefined)
        return handshake;
    state = "discovering";
    const bootstrap = discover();
    if (bootstrap === undefined) {
        state = "unavailable";
        return Promise.reject(new NeoAstraClientError("transport_unavailable", "NeoAstra transport is not available in this document."));
    }
    let timeout;
    try {
        diagnosticQueueLimit = parseBoundedInteger(bootstrap.metadata.maximumDiagnosticQueue, 1, 10_000, "bootstrap diagnostic queue limit");
        timeout = options.handshakeTimeoutMilliseconds ?? parseBoundedInteger(bootstrap.metadata.handshakeTimeoutMilliseconds, 1, 600_000, "bootstrap handshake timeout");
    }
    catch (error) {
        state = "failed";
        const normalized = error instanceof NeoAstraClientError ? error : new NeoAstraClientError("invalid_frame", "The bootstrap transport metadata is invalid.");
        terminalHandshakeError = normalized;
        return Promise.reject(normalized);
    }
    if (!Number.isFinite(timeout) || timeout <= 0 || timeout > 600_000) {
        return Promise.reject(new TypeError("handshakeTimeoutMilliseconds must be greater than zero and no more than ten minutes."));
    }
    state = "handshaking";
    let resolveHandshake;
    let rejectHandshake;
    const pending = new Promise((resolve, reject) => {
        resolveHandshake = resolve;
        rejectHandshake = reject;
    });
    handshake = pending;
    let settled = false;
    let removeReceive = () => { };
    const cleanupReceive = () => { try {
        removeReceive();
    }
    catch { } };
    const timer = globalThis.setTimeout(() => {
        if (settled)
            return;
        settled = true;
        state = "failed";
        cleanupReceive();
        const error = new NeoAstraClientError("handshake_timeout", "The NeoAstra transport handshake timed out.", true);
        emitDiagnostic({ level: "error", code: error.code, message: error.message });
        handshake = undefined;
        rejectHandshake(error);
    }, timeout);
    const fail = (error) => {
        if (settled)
            return;
        settled = true;
        globalThis.clearTimeout(timer);
        cleanupReceive();
        state = "failed";
        handshake = undefined;
        if (!error.retryable)
            terminalHandshakeError = error;
        emitDiagnostic({ level: "error", code: error.code, message: error.message, correlationId: error.correlationId });
        rejectHandshake(error);
    };
    const receive = (value) => {
        if (settled) {
            connection?.accept(value);
            return;
        }
        if (!isRecord(value) || value.neoastra !== 1 || typeof value.kind !== "string") {
            fail(new NeoAstraClientError("invalid_frame", "The host returned an invalid transport frame."));
            return;
        }
        if (value.kind === "close") {
            const code = typeof value.code === "string" && transportErrorCodes.includes(value.code)
                ? value.code
                : "connection_closed";
            fail(new NeoAstraClientError(code, code === "protocol_mismatch" ? "The host uses an incompatible NeoAstra transport protocol." : "The host rejected or closed the transport connection.", code === "handshake_timeout"));
            return;
        }
        if (value.kind !== "hello_ack") {
            fail(new NeoAstraClientError("invalid_frame", "The host returned an unexpected frame before the handshake completed."));
            return;
        }
        const protocol = value.protocol;
        const metadata = value.runtime;
        const limits = value.limits;
        if (!isRecord(protocol) || protocol.major !== PROTOCOL_MAJOR || typeof protocol.minor !== "number") {
            fail(new NeoAstraClientError("protocol_mismatch", "The host uses an incompatible NeoAstra transport protocol."));
            return;
        }
        if (!isRecord(metadata) || !Array.isArray(value.features) || !isRecord(limits)) {
            fail(new NeoAstraClientError("invalid_frame", "The host handshake metadata is invalid."));
            return;
        }
        try {
            const info = parseRuntimeInfo(protocol.minor, value.features, metadata);
            const maximumFrameBytes = parseMaximumFrameBytes(limits.maximumFrameBytes, bootstrap.metadata.maximumFrameBytes);
            const maximumJsonDepth = parseMaximumJsonDepth(limits.maximumJsonDepth);
            diagnosticQueueLimit = Math.min(diagnosticQueueLimit, parseBoundedInteger(limits.maximumDiagnosticQueue, 1, 10_000, "host diagnostic queue limit"));
            connection = new Connection(bootstrap, info, maximumFrameBytes, maximumJsonDepth, cleanupReceive);
            runtimeInfo = info;
            settled = true;
            globalThis.clearTimeout(timer);
            state = "connected";
            resolveHandshake(connection);
        }
        catch (error) {
            fail(error instanceof NeoAstraClientError ? error : new NeoAstraClientError("invalid_frame", "The host handshake metadata is invalid."));
        }
    };
    try {
        const unregister = bootstrap.setReceiveHandler(receive);
        if (typeof unregister !== "function")
            throw new TypeError("The bootstrap transport returned an invalid receive-handler registration.");
        removeReceive = unregister;
        if (settled) {
            if (connection === undefined)
                cleanupReceive();
            return pending;
        }
        bootstrap.send(Object.freeze({
            neoastra: 1,
            kind: "hello",
            protocol: Object.freeze({ major: PROTOCOL_MAJOR, minor: PROTOCOL_MINOR }),
            features: SUPPORTED_FEATURES,
            client: Object.freeze({ name: "@neoastra/client", version: "0.1.0" }),
        }));
    }
    catch (error) {
        fail(normalizeError(error));
    }
    return pending;
}
class Connection {
    bootstrap;
    runtimeInfo;
    maximumFrameBytes;
    maximumJsonDepth;
    removeReceive;
    closed;
    abortController = new AbortController();
    receiveHandler;
    currentState = "connected";
    constructor(bootstrap, runtimeInfo, maximumFrameBytes, maximumJsonDepth, removeReceive) {
        this.bootstrap = bootstrap;
        this.runtimeInfo = runtimeInfo;
        this.maximumFrameBytes = maximumFrameBytes;
        this.maximumJsonDepth = maximumJsonDepth;
        this.removeReceive = removeReceive;
        this.closed = this.abortController.signal;
    }
    get state() { return this.currentState; }
    hasFeature(feature) {
        return this.runtimeInfo.negotiatedFeatures.includes(feature);
    }
    send(frame) {
        if (this.currentState !== "connected")
            throw new NeoAstraClientError("connection_closed", "The transport connection is closed.");
        assertApplicationFrame(frame, this.maximumFrameBytes, this.maximumJsonDepth);
        try {
            this.bootstrap.send(frame);
        }
        catch (error) {
            throw normalizeError(error);
        }
    }
    setReceiveHandler(handler) {
        if (typeof handler !== "function")
            throw new TypeError("A receive handler is required.");
        if (this.receiveHandler !== undefined)
            throw new NeoAstraClientError("internal_transport_error", "A receive handler is already registered.");
        this.receiveHandler = handler;
        return () => { if (this.receiveHandler === handler)
            this.receiveHandler = undefined; };
    }
    close() {
        if (this.currentState === "closed" || this.currentState === "closing")
            return;
        this.currentState = "closing";
        try {
            this.bootstrap.send(Object.freeze({ neoastra: 1, kind: "close" }));
        }
        catch { }
        this.finishClose();
    }
    accept(value) {
        if (!isRecord(value) || value.neoastra !== 1 || typeof value.kind !== "string") {
            emitDiagnostic({ level: "warning", code: "invalid_frame", message: "The host returned an invalid transport frame." });
            return;
        }
        if (value.kind === "close") {
            this.finishClose();
            return;
        }
        if (value.kind === "diagnostic") {
            emitHostDiagnostic(value);
            return;
        }
        try {
            assertApplicationFrame(value, this.maximumFrameBytes, this.maximumJsonDepth);
            this.receiveHandler?.(Object.freeze({ ...value }));
        }
        catch (error) {
            const normalized = normalizeError(error);
            emitDiagnostic({ level: "warning", code: normalized.code, message: normalized.message, correlationId: normalized.correlationId });
        }
    }
    finishClose() {
        if (this.currentState === "closed")
            return;
        this.currentState = "closed";
        state = "closed";
        this.removeReceive();
        this.receiveHandler = undefined;
        this.abortController.abort(new NeoAstraClientError("connection_closed", "The transport connection is closed."));
    }
}
function parseRuntimeInfo(minor, features, value) {
    const platform = value.platform;
    const backend = value.backend;
    if (!((platform === "windows" && backend === "webview2") ||
        (platform === "macos" && backend === "wkwebview") ||
        (platform === "linux" && backend === "webkitgtk")) ||
        typeof value.viewLabel !== "string" || value.viewLabel.length === 0 || value.viewLabel.length > 128 ||
        typeof value.documentSessionId !== "string" || value.documentSessionId.length === 0 || value.documentSessionId.length > 128 ||
        !Number.isSafeInteger(minor) || minor < 0 || typeof value.wholeViewTrust !== "boolean") {
        throw new NeoAstraClientError("invalid_frame", "The host runtime metadata is invalid.");
    }
    const negotiated = features.filter((item) => typeof item === "string" && SUPPORTED_FEATURES.includes(item));
    if (negotiated.length !== features.length) {
        emitDiagnostic({ level: "warning", code: "unknown_feature", message: "The host advertised an unknown transport feature; it was ignored." });
    }
    return Object.freeze({
        available: true,
        protocolMajor: PROTOCOL_MAJOR,
        protocolMinor: Math.min(PROTOCOL_MINOR, minor),
        negotiatedFeatures: Object.freeze(negotiated),
        viewLabel: value.viewLabel,
        documentSessionId: value.documentSessionId,
        platform,
        backend,
        wholeViewTrust: value.wholeViewTrust,
    });
}
function parseMaximumFrameBytes(value, bootstrapLimit) {
    if (!Number.isSafeInteger(value) || value <= 0)
        throw new NeoAstraClientError("invalid_frame", "The host frame limit is invalid.");
    const trustedBootstrapLimit = parseBoundedInteger(bootstrapLimit, 1, HARD_MAXIMUM_FRAME_BYTES, "bootstrap frame limit");
    return Math.min(value, trustedBootstrapLimit, HARD_MAXIMUM_FRAME_BYTES);
}
function parseMaximumJsonDepth(value) {
    if (!Number.isSafeInteger(value) || value < 1 || value > 128) {
        throw new NeoAstraClientError("invalid_frame", "The host JSON depth limit is invalid.");
    }
    return value;
}
function normalizeError(error) {
    if (error instanceof NeoAstraClientError)
        return error;
    if (isRecord(error) && typeof error.code === "string" &&
        transportErrorCodes.includes(error.code)) {
        return new NeoAstraClientError(error.code, typeof error.message === "string" ? error.message : "The browser transport failed.");
    }
    return new NeoAstraClientError("internal_transport_error", "The browser transport failed.");
}
function parseBoundedInteger(value, minimum, maximum, name) {
    if (!Number.isSafeInteger(value) || value < minimum || value > maximum) {
        throw new NeoAstraClientError("invalid_frame", `The ${name} is invalid.`);
    }
    return value;
}
function emitDiagnostic(value) {
    if (diagnosticQueue.length === diagnosticQueueLimit) {
        const lowSeverity = diagnosticQueue.findIndex(item => item.level === "debug" || item.level === "information");
        diagnosticQueue.splice(lowSeverity >= 0 ? lowSeverity : 0, 1);
    }
    const frozen = Object.freeze({ ...value });
    diagnosticQueue.push(frozen);
    for (const listener of diagnosticListeners) {
        try {
            listener(frozen);
        }
        catch { }
    }
}
function emitHostDiagnostic(value) {
    const level = value.level;
    if ((level !== "debug" && level !== "information" && level !== "warning" && level !== "error") ||
        typeof value.code !== "string" || typeof value.message !== "string")
        return;
    emitDiagnostic({ level, code: value.code, message: value.message, correlationId: typeof value.correlationId === "string" ? value.correlationId : undefined });
}
export { NeoRpcClient, NeoRpcError, invoke, invokeChannel, rpcClient, subscribe, } from "./rpc.js";
//# sourceMappingURL=index.js.map
import {
  DEFAULT_DIAGNOSTIC_QUEUE_LIMIT,
  HARD_MAXIMUM_FRAME_BYTES,
  NeoAstraClientError,
  PROTOCOL_MAJOR,
  PROTOCOL_MINOR,
  SUPPORTED_FEATURES,
  assertApplicationFrame,
  isRecord,
  type NeoAstraConnectionState,
} from "./shared.js";

export { NeoAstraClientError } from "./shared.js";
export type { NeoAstraConnectionState } from "./shared.js";

export interface NeoAstraRuntimeInfo {
  readonly available: true;
  readonly protocolMajor: number;
  readonly protocolMinor: number;
  readonly negotiatedFeatures: readonly string[];
  readonly viewLabel: string;
  readonly documentSessionId: string;
  readonly platform: "windows" | "macos" | "linux";
  readonly backend: "webview2" | "wkwebview" | "webkitgtk";
  readonly wholeViewTrust: boolean;
}

export interface NeoAstraTransportDiagnostic {
  readonly level: "debug" | "information" | "warning" | "error";
  readonly code: string;
  readonly message: string;
  readonly correlationId?: string;
}

export interface ConnectOptions {
  readonly handshakeTimeoutMilliseconds?: number;
}

export interface NeoAstraConnection {
  readonly runtimeInfo: NeoAstraRuntimeInfo;
  readonly state: NeoAstraConnectionState;
  readonly closed: AbortSignal;
  hasFeature(feature: string): boolean;
  send(frame: unknown): void;
  setReceiveHandler(handler: (frame: Readonly<Record<string, unknown>>) => void): () => void;
  close(): void;
}

interface BootstrapMetadata {
  readonly platform: NeoAstraRuntimeInfo["platform"];
  readonly backend: NeoAstraRuntimeInfo["backend"];
  readonly viewLabel: string;
  readonly wholeViewTrust: boolean;
  readonly maximumFrameBytes: number;
  readonly maximumDiagnosticQueue: number;
  readonly handshakeTimeoutMilliseconds: number;
}

interface BootstrapTransport {
  readonly metadata: BootstrapMetadata;
  send(frame: Readonly<Record<string, unknown>>): void;
  setReceiveHandler(handler: (frame: unknown) => void): () => void;
}

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
let state: NeoAstraConnectionState = "unavailable";
let connection: Connection | undefined;
let handshake: Promise<NeoAstraConnection> | undefined;
let terminalHandshakeError: NeoAstraClientError | undefined;
let runtimeInfo: NeoAstraRuntimeInfo | undefined;
const diagnosticListeners = new Set<(value: NeoAstraTransportDiagnostic) => void>();
const diagnosticQueue: NeoAstraTransportDiagnostic[] = [];
let diagnosticQueueLimit = DEFAULT_DIAGNOSTIC_QUEUE_LIMIT;

function discover(): BootstrapTransport | undefined {
  const candidate = (globalThis as Record<PropertyKey, unknown>)[transportKey];
  if (!isRecord(candidate) || !isRecord(candidate.metadata) || typeof candidate.send !== "function" || typeof candidate.setReceiveHandler !== "function") {
    return undefined;
  }
  return candidate as unknown as BootstrapTransport;
}

export function isAvailable(): boolean {
  return discover() !== undefined;
}

export function getRuntimeInfo(): NeoAstraRuntimeInfo | undefined {
  return runtimeInfo;
}

export function onDiagnostic(listener: (value: NeoAstraTransportDiagnostic) => void): () => void {
  if (typeof listener !== "function") throw new TypeError("A diagnostic listener is required.");
  diagnosticListeners.add(listener);
  for (const value of diagnosticQueue) {
    try { listener(value); } catch { }
  }
  return () => diagnosticListeners.delete(listener);
}

export function connect(options: ConnectOptions = {}): Promise<NeoAstraConnection> {
  if (connection?.state === "connected") return Promise.resolve(connection);
  if (connection !== undefined) return Promise.reject(new NeoAstraClientError("connection_closed", "The transport connection is closed."));
  if (terminalHandshakeError !== undefined) return Promise.reject(terminalHandshakeError);
  if (handshake !== undefined) return handshake;

  state = "discovering";
  const bootstrap = discover();
  if (bootstrap === undefined) {
    state = "unavailable";
    return Promise.reject(new NeoAstraClientError("transport_unavailable", "NeoAstra transport is not available in this document."));
  }

  let timeout: number;
  try {
    diagnosticQueueLimit = parseBoundedInteger(bootstrap.metadata.maximumDiagnosticQueue, 1, 10_000, "bootstrap diagnostic queue limit");
    timeout = options.handshakeTimeoutMilliseconds ?? parseBoundedInteger(
      bootstrap.metadata.handshakeTimeoutMilliseconds,
      1,
      600_000,
      "bootstrap handshake timeout");
  } catch (error) {
    state = "failed";
    const normalized = error instanceof NeoAstraClientError ? error : new NeoAstraClientError("invalid_frame", "The bootstrap transport metadata is invalid.");
    terminalHandshakeError = normalized;
    return Promise.reject(normalized);
  }
  if (!Number.isFinite(timeout) || timeout <= 0 || timeout > 600_000) {
    return Promise.reject(new TypeError("handshakeTimeoutMilliseconds must be greater than zero and no more than ten minutes."));
  }

  state = "handshaking";
  let resolveHandshake!: (value: NeoAstraConnection) => void;
  let rejectHandshake!: (reason: NeoAstraClientError) => void;
  const pending = new Promise<NeoAstraConnection>((resolve, reject) => {
    resolveHandshake = resolve;
    rejectHandshake = reject;
  });
  handshake = pending;
  let settled = false;
  let removeReceive = (): void => {};
  const cleanupReceive = (): void => { try { removeReceive(); } catch { } };
  const timer = globalThis.setTimeout(() => {
    if (settled) return;
    settled = true;
    state = "failed";
    cleanupReceive();
    const error = new NeoAstraClientError("handshake_timeout", "The NeoAstra transport handshake timed out.", true);
    emitDiagnostic({ level: "error", code: error.code, message: error.message });
    handshake = undefined;
    rejectHandshake(error);
  }, timeout);

  const fail = (error: NeoAstraClientError): void => {
    if (settled) return;
    settled = true;
    globalThis.clearTimeout(timer);
    cleanupReceive();
    state = "failed";
    handshake = undefined;
    if (!error.retryable) terminalHandshakeError = error;
    emitDiagnostic({ level: "error", code: error.code, message: error.message, correlationId: error.correlationId });
    rejectHandshake(error);
  };

  const receive = (value: unknown): void => {
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
      fail(new NeoAstraClientError(
        code,
        code === "protocol_mismatch" ? "The host uses an incompatible NeoAstra transport protocol." : "The host rejected or closed the transport connection.",
        code === "handshake_timeout"));
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
    } catch (error) {
      fail(error instanceof NeoAstraClientError ? error : new NeoAstraClientError("invalid_frame", "The host handshake metadata is invalid."));
    }
  };

  try {
    const unregister = bootstrap.setReceiveHandler(receive);
    if (typeof unregister !== "function") throw new TypeError("The bootstrap transport returned an invalid receive-handler registration.");
    removeReceive = unregister;
    if (settled) {
      if (connection === undefined) cleanupReceive();
      return pending;
    }
    bootstrap.send(Object.freeze({
      neoastra: 1,
      kind: "hello",
      protocol: Object.freeze({ major: PROTOCOL_MAJOR, minor: PROTOCOL_MINOR }),
      features: SUPPORTED_FEATURES,
      client: Object.freeze({ name: "@neoastra/client", version: "0.1.0" }),
    }));
  } catch (error) {
    fail(normalizeError(error));
  }
  return pending;
}

class Connection implements NeoAstraConnection {
  readonly closed: AbortSignal;
  private readonly abortController = new AbortController();
  private receiveHandler: ((frame: Readonly<Record<string, unknown>>) => void) | undefined;
  private currentState: NeoAstraConnectionState = "connected";

  constructor(
    private readonly bootstrap: BootstrapTransport,
    readonly runtimeInfo: NeoAstraRuntimeInfo,
    private readonly maximumFrameBytes: number,
    private readonly maximumJsonDepth: number,
    private readonly removeReceive: () => void,
  ) {
    this.closed = this.abortController.signal;
  }

  get state(): NeoAstraConnectionState { return this.currentState; }

  hasFeature(feature: string): boolean {
    return this.runtimeInfo.negotiatedFeatures.includes(feature);
  }

  send(frame: unknown): void {
    if (this.currentState !== "connected") throw new NeoAstraClientError("connection_closed", "The transport connection is closed.");
    assertApplicationFrame(frame, this.maximumFrameBytes, this.maximumJsonDepth);
    try {
      this.bootstrap.send(frame);
    } catch (error) {
      throw normalizeError(error);
    }
  }

  setReceiveHandler(handler: (frame: Readonly<Record<string, unknown>>) => void): () => void {
    if (typeof handler !== "function") throw new TypeError("A receive handler is required.");
    if (this.receiveHandler !== undefined) throw new NeoAstraClientError("internal_transport_error", "A receive handler is already registered.");
    this.receiveHandler = handler;
    return () => { if (this.receiveHandler === handler) this.receiveHandler = undefined; };
  }

  close(): void {
    if (this.currentState === "closed" || this.currentState === "closing") return;
    this.currentState = "closing";
    try { this.bootstrap.send(Object.freeze({ neoastra: 1, kind: "close" })); } catch { }
    this.finishClose();
  }

  accept(value: unknown): void {
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
    } catch (error) {
      const normalized = normalizeError(error);
      emitDiagnostic({ level: "warning", code: normalized.code, message: normalized.message, correlationId: normalized.correlationId });
    }
  }

  private finishClose(): void {
    if (this.currentState === "closed") return;
    this.currentState = "closed";
    state = "closed";
    this.removeReceive();
    this.receiveHandler = undefined;
    this.abortController.abort(new NeoAstraClientError("connection_closed", "The transport connection is closed."));
  }
}

function parseRuntimeInfo(minor: number, features: unknown[], value: Record<string, unknown>): NeoAstraRuntimeInfo {
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
  const negotiated = features.filter((item): item is string => typeof item === "string" && SUPPORTED_FEATURES.includes(item as typeof SUPPORTED_FEATURES[number]));
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

function parseMaximumFrameBytes(value: unknown, bootstrapLimit: number): number {
  if (!Number.isSafeInteger(value) || (value as number) <= 0) throw new NeoAstraClientError("invalid_frame", "The host frame limit is invalid.");
  const trustedBootstrapLimit = parseBoundedInteger(bootstrapLimit, 1, HARD_MAXIMUM_FRAME_BYTES, "bootstrap frame limit");
  return Math.min(value as number, trustedBootstrapLimit, HARD_MAXIMUM_FRAME_BYTES);
}

function parseMaximumJsonDepth(value: unknown): number {
  if (!Number.isSafeInteger(value) || (value as number) < 1 || (value as number) > 128) {
    throw new NeoAstraClientError("invalid_frame", "The host JSON depth limit is invalid.");
  }
  return value as number;
}

function normalizeError(error: unknown): NeoAstraClientError {
  if (error instanceof NeoAstraClientError) return error;
  if (isRecord(error) && typeof error.code === "string" &&
      transportErrorCodes.includes(error.code)) {
    return new NeoAstraClientError(error.code, typeof error.message === "string" ? error.message : "The browser transport failed.");
  }
  return new NeoAstraClientError("internal_transport_error", "The browser transport failed.");
}

function parseBoundedInteger(value: unknown, minimum: number, maximum: number, name: string): number {
  if (!Number.isSafeInteger(value) || (value as number) < minimum || (value as number) > maximum) {
    throw new NeoAstraClientError("invalid_frame", `The ${name} is invalid.`);
  }
  return value as number;
}

function emitDiagnostic(value: NeoAstraTransportDiagnostic): void {
  if (diagnosticQueue.length === diagnosticQueueLimit) {
    const lowSeverity = diagnosticQueue.findIndex(item => item.level === "debug" || item.level === "information");
    diagnosticQueue.splice(lowSeverity >= 0 ? lowSeverity : 0, 1);
  }
  const frozen = Object.freeze({ ...value });
  diagnosticQueue.push(frozen);
  for (const listener of diagnosticListeners) {
    try { listener(frozen); } catch { }
  }
}

function emitHostDiagnostic(value: Record<string, unknown>): void {
  const level = value.level;
  if ((level !== "debug" && level !== "information" && level !== "warning" && level !== "error") ||
      typeof value.code !== "string" || typeof value.message !== "string") return;
  emitDiagnostic({ level, code: value.code, message: value.message, correlationId: typeof value.correlationId === "string" ? value.correlationId : undefined });
}

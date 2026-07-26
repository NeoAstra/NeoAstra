import type {
  ConnectOptions,
  NeoAstraConnection,
  NeoAstraRuntimeInfo,
  NeoAstraTransportDiagnostic,
} from "./index.js";
import {
  DEFAULT_MAXIMUM_FRAME_BYTES,
  NeoAstraClientError,
  PROTOCOL_MAJOR,
  PROTOCOL_MINOR,
  SUPPORTED_FEATURES,
  assertApplicationFrame,
  isRecord,
  type NeoAstraConnectionState,
} from "./shared.js";

export interface MockScheduler {
  setTimeout(callback: () => void, delayMilliseconds: number): unknown;
  clearTimeout(handle: unknown): void;
}

export interface MockTransportOptions {
  readonly runtimeInfo?: Partial<Omit<NeoAstraRuntimeInfo, "available">>;
  readonly negotiatedFeatures?: readonly string[];
  readonly connectDelayMilliseconds?: number;
  readonly protocolMajor?: number;
  readonly maximumFrameBytes?: number;
  readonly maximumJsonDepth?: number;
  readonly scheduler?: MockScheduler;
  readonly idFactory?: () => string;
}

export interface MockNeoAstraClient {
  isAvailable(): boolean;
  getRuntimeInfo(): NeoAstraRuntimeInfo | undefined;
  connect(options?: ConnectOptions): Promise<NeoAstraConnection>;
  onDiagnostic(listener: (value: NeoAstraTransportDiagnostic) => void): () => void;
  readonly outboundFrames: readonly Readonly<Record<string, unknown>>[];
  injectInbound(frame: unknown): void;
  injectMalformed(): void;
  close(): void;
  navigate(overrides?: MockTransportOptions): MockNeoAstraClient;
}

const defaultScheduler: MockScheduler = {
  setTimeout: (callback, delay) => globalThis.setTimeout(callback, delay),
  clearTimeout: handle => globalThis.clearTimeout(handle as ReturnType<typeof setTimeout>),
};

export function createMockClient(options: MockTransportOptions = {}): MockNeoAstraClient {
  return new MockClient(options);
}

class MockClient implements MockNeoAstraClient {
  private readonly options: MockTransportOptions;
  private readonly scheduler: MockScheduler;
  private readonly diagnostics = new Set<(value: NeoAstraTransportDiagnostic) => void>();
  private readonly mutableOutboundFrames: Readonly<Record<string, unknown>>[] = [];
  private currentRuntimeInfo: NeoAstraRuntimeInfo | undefined;
  private handshake: Promise<NeoAstraConnection> | undefined;
  private connection: MockConnection | undefined;
  private active = true;

  constructor(options: MockTransportOptions) {
    this.options = options;
    this.scheduler = options.scheduler ?? defaultScheduler;
  }

  get outboundFrames(): readonly Readonly<Record<string, unknown>>[] { return this.mutableOutboundFrames; }

  isAvailable(): boolean { return this.active; }

  getRuntimeInfo(): NeoAstraRuntimeInfo | undefined { return this.currentRuntimeInfo; }

  connect(connectOptions: ConnectOptions = {}): Promise<NeoAstraConnection> {
    if (!this.active) return Promise.reject(new NeoAstraClientError("transport_unavailable", "The mock document is no longer active."));
    if (this.connection?.state === "connected") return Promise.resolve(this.connection);
    if (this.handshake !== undefined) return this.handshake;
    const timeout = connectOptions.handshakeTimeoutMilliseconds ?? 10_000;
    if (!Number.isFinite(timeout) || timeout <= 0 || timeout > 600_000) {
      return Promise.reject(new TypeError("handshakeTimeoutMilliseconds must be greater than zero and no more than ten minutes."));
    }
    const delay = this.options.connectDelayMilliseconds ?? 0;

    this.mutableOutboundFrames.push(Object.freeze({
      neoastra: 1,
      kind: "hello",
      protocol: Object.freeze({ major: PROTOCOL_MAJOR, minor: PROTOCOL_MINOR }),
      features: SUPPORTED_FEATURES,
      client: Object.freeze({ name: "@neoastra/client", version: "0.1.0" }),
    }));
    this.handshake = new Promise((resolve, reject) => {
      this.scheduler.setTimeout(() => {
        if (!this.active) {
          reject(new NeoAstraClientError("connection_closed", "The mock document was replaced."));
          return;
        }
        if (delay > timeout) {
          this.handshake = undefined;
          reject(new NeoAstraClientError("handshake_timeout", "The mock transport handshake timed out.", true));
          return;
        }
        if ((this.options.protocolMajor ?? PROTOCOL_MAJOR) !== PROTOCOL_MAJOR) {
          reject(new NeoAstraClientError("protocol_mismatch", "The mock host protocol is incompatible."));
          return;
        }
        const id = this.options.idFactory?.() ?? "mock-document-session";
        this.currentRuntimeInfo = makeRuntimeInfo(this.options, id);
        this.connection = new MockConnection(
          this.currentRuntimeInfo,
          this.options.maximumFrameBytes ?? DEFAULT_MAXIMUM_FRAME_BYTES,
          this.options.maximumJsonDepth ?? 32,
          frame => this.mutableOutboundFrames.push(frame),
          diagnostic => this.emitDiagnostic(diagnostic));
        resolve(this.connection);
      }, Math.min(delay, timeout));
    });
    return this.handshake;
  }

  onDiagnostic(listener: (value: NeoAstraTransportDiagnostic) => void): () => void {
    this.diagnostics.add(listener);
    return () => this.diagnostics.delete(listener);
  }

  injectInbound(frame: unknown): void {
    if (!isRecord(frame) || frame.neoastra !== 1 || typeof frame.kind !== "string") {
      this.injectMalformed();
      return;
    }
    this.connection?.accept(Object.freeze({ ...frame }));
  }

  injectMalformed(): void {
    this.emitDiagnostic({ level: "warning", code: "invalid_frame", message: "The mock host injected a malformed frame." });
  }

  close(): void {
    this.active = false;
    this.connection?.hostClose();
  }

  navigate(overrides: MockTransportOptions = {}): MockNeoAstraClient {
    this.close();
    return new MockClient({ ...this.options, ...overrides });
  }

  private emitDiagnostic(value: NeoAstraTransportDiagnostic): void {
    const frozen = Object.freeze({ ...value });
    for (const listener of this.diagnostics) {
      try { listener(frozen); } catch { }
    }
  }
}

class MockConnection implements NeoAstraConnection {
  readonly closed: AbortSignal;
  private readonly controller = new AbortController();
  private currentState: NeoAstraConnectionState = "connected";
  private receiveHandler: ((frame: Readonly<Record<string, unknown>>) => void) | undefined;

  constructor(
    readonly runtimeInfo: NeoAstraRuntimeInfo,
    private readonly maximumFrameBytes: number,
    private readonly maximumJsonDepth: number,
    private readonly record: (frame: Readonly<Record<string, unknown>>) => void,
    private readonly diagnose: (value: NeoAstraTransportDiagnostic) => void,
  ) {
    this.closed = this.controller.signal;
  }

  get state(): NeoAstraConnectionState { return this.currentState; }

  hasFeature(feature: string): boolean { return this.runtimeInfo.negotiatedFeatures.includes(feature); }

  send(frame: unknown): void {
    if (this.currentState !== "connected") throw new NeoAstraClientError("connection_closed", "The mock connection is closed.");
    assertApplicationFrame(frame, this.maximumFrameBytes, this.maximumJsonDepth);
    this.record(Object.freeze({ ...(frame as Record<string, unknown>) }));
  }

  setReceiveHandler(handler: (frame: Readonly<Record<string, unknown>>) => void): () => void {
    if (this.receiveHandler !== undefined) throw new NeoAstraClientError("internal_transport_error", "A receive handler is already registered.");
    this.receiveHandler = handler;
    return () => { if (this.receiveHandler === handler) this.receiveHandler = undefined; };
  }

  close(): void {
    if (this.currentState !== "connected") return;
    this.currentState = "closing";
    this.record(Object.freeze({ neoastra: 1, kind: "close" }));
    this.finishClose();
  }

  accept(frame: Readonly<Record<string, unknown>>): void {
    if (this.currentState !== "connected") return;
    if (frame.kind === "close") {
      this.finishClose();
      return;
    }
    if (frame.kind === "diagnostic") {
      const level = frame.level;
      if ((level === "debug" || level === "information" || level === "warning" || level === "error") &&
          typeof frame.code === "string" && typeof frame.message === "string") {
        this.diagnose({ level, code: frame.code, message: frame.message, correlationId: typeof frame.correlationId === "string" ? frame.correlationId : undefined });
      }
      return;
    }
    try {
      assertApplicationFrame(frame, this.maximumFrameBytes, this.maximumJsonDepth);
      this.receiveHandler?.(frame);
    } catch (error) {
      const value = error instanceof NeoAstraClientError
        ? error
        : new NeoAstraClientError("internal_transport_error", "The mock transport failed to validate an inbound frame.");
      this.diagnose({ level: "warning", code: value.code, message: value.message, correlationId: value.correlationId });
    }
  }

  hostClose(): void {
    this.diagnose({ level: "information", code: "connection_closed", message: "The mock host closed the connection." });
    this.finishClose();
  }

  private finishClose(): void {
    if (this.currentState === "closed") return;
    this.currentState = "closed";
    this.receiveHandler = undefined;
    this.controller.abort(new NeoAstraClientError("connection_closed", "The mock connection is closed."));
  }
}

function makeRuntimeInfo(options: MockTransportOptions, id: string): NeoAstraRuntimeInfo {
  const source = options.runtimeInfo ?? {};
  return Object.freeze({
    available: true,
    protocolMajor: source.protocolMajor ?? PROTOCOL_MAJOR,
    protocolMinor: source.protocolMinor ?? PROTOCOL_MINOR,
    negotiatedFeatures: Object.freeze([...(options.negotiatedFeatures ?? source.negotiatedFeatures ?? SUPPORTED_FEATURES)]),
    viewLabel: source.viewLabel ?? "mock",
    documentSessionId: source.documentSessionId ?? id,
    platform: source.platform ?? "windows",
    backend: source.backend ?? "webview2",
    wholeViewTrust: source.wholeViewTrust ?? false,
  });
}

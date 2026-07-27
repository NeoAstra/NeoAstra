import type {
  ConnectOptions,
  NeoAstraConnection,
  NeoAstraRuntimeInfo,
  NeoAstraTransportDiagnostic,
} from "./index.js";
import { NeoRpcClient, NeoRpcError, type NeoRpcErrorValue } from "./rpc.js";
import { createDesktopClient, type DesktopRpc } from "./desktop.js";
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

export interface MockRpcInvocation {
  readonly command: string;
  readonly args: unknown;
  readonly signal: AbortSignal;
}

/** A small capability-neutral desktop mock. Applications must opt in each command result explicitly. */
export interface MockDesktopHarness {
  readonly client: ReturnType<typeof createDesktopClient>;
  readonly invocations: readonly { readonly command: string; readonly args: unknown }[];
  setResult(command: string, result: unknown): void;
  emit(event: string, value: unknown): void;
}

export function createMockDesktop(): MockDesktopHarness {
  const results = new Map<string, unknown>();
  const invocations: { command: string; args: unknown }[] = [];
  const subscriptions = new Map<string, Set<(value: unknown) => void>>();
  const rpc: DesktopRpc = {
    invoke: async <TRequest, TResult>(command: string, args: TRequest): Promise<TResult> => {
      invocations.push(Object.freeze({ command, args }));
      if (!results.has(command)) throw new NeoRpcError({ code: "permission_denied", message: "The mock command has no explicit result.", retryable: false });
      return results.get(command) as TResult;
    },
    subscribe: async <T>(event: string, handler: (value: T) => void) => {
      let values = subscriptions.get(event); if (values === undefined) { values = new Set(); subscriptions.set(event, values); }
      const untyped = handler as (value: unknown) => void; values.add(untyped);
      return async () => { values!.delete(untyped); };
    },
  };
  return {
    client: createDesktopClient(rpc),
    invocations,
    setResult: (command, result) => results.set(command, result),
    emit: (event, value) => { for (const handler of subscriptions.get(event) ?? []) { try { handler(value); } catch { } } },
  };
}

export type MockRpcHandler = (invocation: MockRpcInvocation) => unknown | Promise<unknown>;

export interface MockRpcHarness {
  readonly client: NeoRpcClient;
  readonly outboundFrames: readonly Readonly<Record<string, unknown>>[];
  register(command: string, handler: MockRpcHandler): () => void;
  emit(event: string, value: unknown): void;
  close(): void;
}

export function createMockRpcHarness(options: MockTransportOptions = {}): MockRpcHarness {
  const handlers = new Map<string, MockRpcHandler>();
  const subscriptions = new Map<string, { event: string; sequence: number }>();
  const invocations = new Map<string, AbortController>();
  const frames: Readonly<Record<string, unknown>>[] = [];
  let receiver: ((frame: Readonly<Record<string, unknown>>) => void) | undefined;
  const closed = new AbortController();
  const runtimeInfo = makeRuntimeInfo(options, options.idFactory?.() ?? "mock-rpc-session");

  const connection: NeoAstraConnection = {
    runtimeInfo,
    state: "connected",
    closed: closed.signal,
    hasFeature: feature => runtimeInfo.negotiatedFeatures.includes(feature),
    setReceiveHandler(handler) {
      if (receiver !== undefined) throw new NeoAstraClientError("internal_transport_error", "A receive handler is already registered.");
      receiver = handler;
      return () => { if (receiver === handler) receiver = undefined; };
    },
    send(frame) {
      assertApplicationFrame(frame, options.maximumFrameBytes ?? DEFAULT_MAXIMUM_FRAME_BYTES, options.maximumJsonDepth ?? 32);
      const value = Object.freeze({ ...(frame as Record<string, unknown>) });
      frames.push(value);
      queueMicrotask(() => dispatch(value));
    },
    close() { if (!closed.signal.aborted) closed.abort(); },
  };
  const client = new NeoRpcClient(connection, { idFactory: options.idFactory });

  async function dispatch(frame: Readonly<Record<string, unknown>>): Promise<void> {
    if (closed.signal.aborted || typeof frame.kind !== "string") return;
    if (frame.kind === "invoke" && typeof frame.id === "string" && typeof frame.command === "string") {
      const controller = new AbortController();
      invocations.set(frame.id, controller);
      const handler = handlers.get(frame.command);
      if (handler === undefined) {
        receiver?.(resultError(frame.id, { code: "command_not_found", message: "The mock command is not registered.", retryable: false }));
        invocations.delete(frame.id);
        return;
      }
      try {
        const value = await handler({ command: frame.command, args: frame.args, signal: controller.signal });
        if (!controller.signal.aborted) receiver?.(Object.freeze({ neoastra: 1, kind: "result", id: frame.id, ok: true, value }));
      } catch (error) {
        const mapped = error instanceof NeoRpcError
          ? { code: error.code, message: error.message, retryable: error.retryable, correlationId: error.correlationId }
          : { code: "internal_error", message: "The mock command failed.", retryable: false };
        receiver?.(resultError(frame.id, mapped));
      } finally { invocations.delete(frame.id); }
    } else if (frame.kind === "cancel" && typeof frame.id === "string") {
      invocations.get(frame.id)?.abort();
    } else if (frame.kind === "subscribe" && typeof frame.id === "string" && typeof frame.event === "string") {
      subscriptions.set(frame.id, { event: frame.event, sequence: 0 });
      receiver?.(Object.freeze({ neoastra: 1, kind: "subscribed", id: frame.id }));
    } else if (frame.kind === "unsubscribe" && typeof frame.id === "string") {
      subscriptions.delete(frame.id);
    }
  }

  return {
    client,
    outboundFrames: frames,
    register(command, handler) {
      if (typeof command !== "string" || command.length === 0) throw new TypeError("A command is required.");
      if (typeof handler !== "function") throw new TypeError("A handler is required.");
      if (handlers.has(command)) throw new TypeError("The mock command is already registered.");
      handlers.set(command, handler);
      return () => { if (handlers.get(command) === handler) handlers.delete(command); };
    },
    emit(event, value) {
      for (const [id, subscription] of subscriptions) {
        if (subscription.event !== event) continue;
        receiver?.(Object.freeze({ neoastra: 1, kind: "event", subscription: id, sequence: ++subscription.sequence, value }));
      }
    },
    close() {
      for (const controller of invocations.values()) controller.abort();
      invocations.clear(); subscriptions.clear(); connection.close(); client.close();
    },
  };
}

function resultError(id: string, error: NeoRpcErrorValue): Readonly<Record<string, unknown>> {
  return Object.freeze({ neoastra: 1, kind: "result", id, ok: false, error: Object.freeze({ ...error }) });
}

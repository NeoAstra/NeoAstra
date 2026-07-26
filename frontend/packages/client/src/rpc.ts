import { NeoAstraClientError, type NeoAstraConnection, connect } from "./index.js";
import { isRecord } from "./shared.js";

export interface NeoRpcCallOptions {
  /** Cancels an invocation or a pending subscription handshake. */
  readonly signal?: AbortSignal;
  /** Bounds invocation completion or subscription acknowledgement to at most ten minutes. */
  readonly timeoutMilliseconds?: number;
  /** Requires the host to accept this generated contract hash. */
  readonly contractHash?: string;
}

export interface NeoRpcErrorValue {
  readonly code: string;
  readonly message: string;
  readonly correlationId?: string;
  readonly retryable: boolean;
}

export class NeoRpcError extends Error implements NeoRpcErrorValue {
  readonly code: string;
  readonly correlationId?: string;
  readonly retryable: boolean;

  constructor(value: NeoRpcErrorValue) {
    super(value.message);
    this.name = "NeoRpcError";
    this.code = value.code;
    this.correlationId = value.correlationId;
    this.retryable = value.retryable;
  }
}

export type NeoRpcEventHandler<T> = (value: T) => void;
export type NeoRpcUnsubscribe = () => Promise<void>;

export interface NeoRpcClientOptions {
  readonly idFactory?: () => string;
  readonly maximumBufferedChannelItems?: number;
}

interface PendingCall {
  resolve(value: unknown): void;
  reject(error: Error): void;
  cleanup(): void;
}

interface PendingSubscription {
  resolve(value: NeoRpcUnsubscribe): void;
  reject(error: Error): void;
  handler(value: unknown): void;
}

interface ActiveSubscription {
  handler(value: unknown): void;
  sequence: number;
}

interface ChannelState<T> {
  readonly values: Array<IteratorResult<T>>;
  readonly waiters: Array<{ resolve(value: IteratorResult<T>): void; reject(error: Error): void }>;
  error?: Error;
  sequence: number;
  closed: boolean;
}

export class NeoRpcClient {
  private readonly pending = new Map<string, PendingCall>();
  private readonly pendingSubscriptions = new Map<string, PendingSubscription>();
  private readonly subscriptions = new Map<string, ActiveSubscription>();
  private readonly channels = new Map<string, ChannelState<unknown>>();
  private readonly removeReceive: () => void;
  private readonly connectionClosed: () => void;
  private detached = false;
  private counter = 0;
  private closed = false;
  private readonly maximumBufferedChannelItems: number;

  constructor(readonly connection: NeoAstraConnection, private readonly options: NeoRpcClientOptions = {}) {
    if (connection.state !== "connected") throw new NeoAstraClientError("connection_closed", "RPC requires a connected transport.");
    const maximum = options.maximumBufferedChannelItems ?? 64;
    if (!Number.isSafeInteger(maximum) || maximum < 1 || maximum > 4096) throw new RangeError("maximumBufferedChannelItems must be between 1 and 4096.");
    this.maximumBufferedChannelItems = maximum;
    this.removeReceive = connection.setReceiveHandler(frame => this.receive(frame));
    this.connectionClosed = () => this.failAll(new NeoRpcError({ code: "connection_closed", message: "The RPC connection closed.", retryable: true }));
    connection.closed.addEventListener("abort", this.connectionClosed, { once: true });
    if (connection.closed.aborted || connection.state !== "connected") {
      this.detach();
      throw new NeoAstraClientError("connection_closed", "The RPC connection closed while RPC was attaching.");
    }
  }

  invoke<TRequest, TResult>(command: string, args: TRequest, options: NeoRpcCallOptions = {}): Promise<TResult> {
    assertWireName(command, "command");
    assertContractHash(options.contractHash);
    if (options.signal?.aborted) return Promise.reject(abortError());
    const id = this.nextId("req");
    return new Promise<TResult>((resolve, reject) => {
      let timer: ReturnType<typeof setTimeout> | undefined;
      const abort = (): void => {
        if (!this.pending.delete(id)) return;
        try { this.connection.send(Object.freeze({ neoastra: 1, kind: "cancel", id })); } catch { }
        cleanup();
        reject(abortError());
      };
      const cleanup = (): void => {
        options.signal?.removeEventListener("abort", abort);
        if (timer !== undefined) clearTimeout(timer);
      };
      this.pending.set(id, { resolve: value => resolve(value as TResult), reject, cleanup });
      options.signal?.addEventListener("abort", abort, { once: true });
      if (options.signal?.aborted) { abort(); return; }
      if (options.timeoutMilliseconds !== undefined) {
        if (!Number.isFinite(options.timeoutMilliseconds) || options.timeoutMilliseconds <= 0 || options.timeoutMilliseconds > 600_000) {
          this.pending.delete(id); cleanup(); reject(new TypeError("timeoutMilliseconds must be greater than zero and no more than ten minutes.")); return;
        }
        timer = setTimeout(abort, options.timeoutMilliseconds);
      }
      try { this.connection.send(Object.freeze({ neoastra: 1, kind: "invoke", id, command, args, contract: options.contractHash })); }
      catch (error) { this.pending.delete(id); cleanup(); reject(normalize(error)); }
    });
  }

  subscribe<T>(event: string, handler: NeoRpcEventHandler<T>, options: NeoRpcCallOptions = {}): Promise<NeoRpcUnsubscribe> {
    assertWireName(event, "event");
    assertContractHash(options.contractHash);
    if (typeof handler !== "function") return Promise.reject(new TypeError("An event handler is required."));
    if (options.signal?.aborted) return Promise.reject(abortError());
    const id = this.nextId("sub");
    return new Promise((resolve, reject) => {
      let timer: ReturnType<typeof setTimeout> | undefined;
      const cleanup = (): void => {
        options.signal?.removeEventListener("abort", abort);
        if (timer !== undefined) clearTimeout(timer);
      };
      const fail = (error: NeoRpcError): void => {
        if (!this.pendingSubscriptions.delete(id)) return;
        try { this.connection.send(Object.freeze({ neoastra: 1, kind: "unsubscribe", id })); } catch { }
        cleanup();
        reject(error);
      };
      const abort = (): void => fail(abortError());
      if (options.timeoutMilliseconds !== undefined && (!Number.isFinite(options.timeoutMilliseconds) || options.timeoutMilliseconds <= 0 || options.timeoutMilliseconds > 600_000)) {
        reject(new TypeError("timeoutMilliseconds must be greater than zero and no more than ten minutes.")); return;
      }
      options.signal?.addEventListener("abort", abort, { once: true });
      this.pendingSubscriptions.set(id, {
        resolve: unsubscribe => { cleanup(); resolve(unsubscribe); },
        reject: error => { cleanup(); reject(error); },
        handler: value => handler(value as T),
      });
      if (options.signal?.aborted) { abort(); return; }
      if (options.timeoutMilliseconds !== undefined) timer = setTimeout(() => fail(new NeoRpcError({ code: "timeout", message: "The RPC subscription acknowledgement timed out.", retryable: true })), options.timeoutMilliseconds);
      try { this.connection.send(Object.freeze({ neoastra: 1, kind: "subscribe", id, event, contract: options.contractHash })); }
      catch (error) { this.pendingSubscriptions.delete(id); cleanup(); reject(normalize(error)); }
    });
  }

  channel<T>(id: string): AsyncIterable<T> {
    assertOpaqueId(id, "channel ID");
    const state = this.ensureChannel(id) as ChannelState<T>;
    const client = this;
    return {
      [Symbol.asyncIterator](): AsyncIterator<T> {
        return {
          async next(): Promise<IteratorResult<T>> {
            if (state!.values.length !== 0) return state!.values.shift()!;
            if (state!.error !== undefined) { client.channels.delete(id); throw state!.error; }
            if (state!.closed) { client.channels.delete(id); return { done: true, value: undefined }; }
            return new Promise((resolve, reject) => state!.waiters.push({ resolve, reject }));
          },
          async return(): Promise<IteratorResult<T>> {
            if (!state!.closed) {
              state!.closed = true;
              client.channels.delete(id);
              for (const waiter of state!.waiters.splice(0)) waiter.resolve({ done: true, value: undefined });
              try { client.connection.send(Object.freeze({ neoastra: 1, kind: "channel_close", channel: id })); } catch { }
            }
            return { done: true, value: undefined };
          },
        };
      },
    };
  }

  closeResource(id: string): void {
    assertOpaqueId(id, "resource ID");
    this.connection.send(Object.freeze({ neoastra: 1, kind: "resource_close", resource: id }));
  }

  close(): void {
    if (this.closed) return;
    this.closed = true;
    for (const id of this.pending.keys()) { try { this.connection.send(Object.freeze({ neoastra: 1, kind: "cancel", id })); } catch { } }
    for (const id of this.pendingSubscriptions.keys()) { try { this.connection.send(Object.freeze({ neoastra: 1, kind: "unsubscribe", id })); } catch { } }
    for (const id of this.subscriptions.keys()) { try { this.connection.send(Object.freeze({ neoastra: 1, kind: "unsubscribe", id })); } catch { } }
    for (const id of this.channels.keys()) { try { this.connection.send(Object.freeze({ neoastra: 1, kind: "channel_close", channel: id })); } catch { } }
    this.detach();
    this.failAll(new NeoRpcError({ code: "connection_closed", message: "The RPC client closed.", retryable: false }));
  }

  private receive(frame: Readonly<Record<string, unknown>>): void {
    switch (frame.kind) {
      case "result": this.receiveResult(frame); break;
      case "subscribed": this.receiveSubscribed(frame); break;
      case "event": this.receiveEvent(frame); break;
      case "channel_item": this.receiveChannelItem(frame); break;
      case "channel_complete": this.receiveChannelTerminal(frame); break;
      case "channel_error": this.receiveChannelTerminal(frame); break;
    }
  }

  private receiveResult(frame: Readonly<Record<string, unknown>>): void {
    if (typeof frame.id !== "string") return;
    const pending = this.pending.get(frame.id);
    if (pending === undefined) return;
    this.pending.delete(frame.id); pending.cleanup();
    if (frame.ok === true) {
      if (isRecord(frame.value) && typeof frame.value.channel === "string" && Object.keys(frame.value).length === 1) {
        try { this.ensureChannel(frame.value.channel); }
        catch { pending.reject(new NeoRpcError({ code: "invalid_request", message: "The host returned an invalid channel ID.", retryable: false })); return; }
      }
      pending.resolve(frame.value);
    }
    else pending.reject(parseError(frame.error));
  }

  private receiveSubscribed(frame: Readonly<Record<string, unknown>>): void {
    if (typeof frame.id !== "string") return;
    const pending = this.pendingSubscriptions.get(frame.id);
    if (pending === undefined) return;
    this.pendingSubscriptions.delete(frame.id);
    if (frame.error !== undefined) { pending.reject(parseError(frame.error)); return; }
    this.subscriptions.set(frame.id, { handler: pending.handler, sequence: 0 });
    pending.resolve(async () => {
      if (!this.subscriptions.delete(frame.id as string)) return;
      this.connection.send(Object.freeze({ neoastra: 1, kind: "unsubscribe", id: frame.id }));
    });
  }

  private receiveEvent(frame: Readonly<Record<string, unknown>>): void {
    if (typeof frame.subscription !== "string" || !Number.isSafeInteger(frame.sequence)) return;
    const state = this.subscriptions.get(frame.subscription);
    if (state === undefined || (frame.sequence as number) !== state.sequence + 1) return;
    state.sequence = frame.sequence as number;
    try { state.handler(frame.value); } catch { }
  }

  private receiveChannelItem(frame: Readonly<Record<string, unknown>>): void {
    if (typeof frame.channel !== "string" || !Number.isSafeInteger(frame.sequence)) return;
    const state = this.channels.get(frame.channel);
    if (state === undefined || state.closed || frame.sequence !== state.sequence + 1) return;
    state.sequence = frame.sequence as number;
    const value: IteratorResult<unknown> = { done: false, value: frame.value };
    const waiter = state.waiters.shift();
    if (waiter !== undefined) waiter.resolve(value);
    else if (state.values.length < this.maximumBufferedChannelItems) state.values.push(value);
    else {
      state.error = new NeoRpcError({ code: "too_many_requests", message: "The frontend channel buffer limit was exhausted.", retryable: false });
      state.closed = true;
      for (const pending of state.waiters.splice(0)) pending.reject(state.error);
      try { this.connection.send(Object.freeze({ neoastra: 1, kind: "channel_close", channel: frame.channel })); } catch { }
      return;
    }
    try { this.connection.send(Object.freeze({ neoastra: 1, kind: "channel_ack", channel: frame.channel, sequence: frame.sequence })); } catch { }
  }

  private receiveChannelTerminal(frame: Readonly<Record<string, unknown>>): void {
    if (typeof frame.channel !== "string") return;
    const state = this.channels.get(frame.channel);
    if (state === undefined) return;
    state.closed = true;
    if (frame.kind === "channel_error") state.error = parseError(frame.error);
    const terminal: IteratorResult<unknown> = { done: true, value: undefined };
    for (const waiter of state.waiters.splice(0)) {
      if (state.error !== undefined) waiter.reject(state.error); else waiter.resolve(terminal);
    }
  }

  private failAll(error: Error): void {
    this.closed = true;
    this.detach();
    for (const pending of this.pending.values()) { pending.cleanup(); pending.reject(error); }
    for (const pending of this.pendingSubscriptions.values()) pending.reject(error);
    for (const state of this.channels.values()) { state.error = error; state.closed = true; state.values.length = 0; for (const waiter of state.waiters.splice(0)) waiter.reject(error); }
    this.pending.clear(); this.pendingSubscriptions.clear(); this.subscriptions.clear(); this.channels.clear();
  }

  private detach(): void {
    if (this.detached) return;
    this.detached = true;
    this.connection.closed.removeEventListener("abort", this.connectionClosed);
    this.removeReceive();
  }

  private nextId(prefix: string): string {
    if (this.closed) throw new NeoRpcError({ code: "connection_closed", message: "The RPC client is closed.", retryable: false });
    const generated = this.options.idFactory?.() ?? `${prefix}-${Date.now().toString(36)}-${(++this.counter).toString(36)}`;
    assertOpaqueId(generated, "generated RPC ID");
    return generated;
  }

  private ensureChannel(id: string): ChannelState<unknown> {
    assertOpaqueId(id, "channel ID");
    let state = this.channels.get(id);
    if (state === undefined) {
      state = { values: [], waiters: [], sequence: 0, closed: false };
      this.channels.set(id, state);
    }
    return state;
  }
}

let defaultClient: Promise<NeoRpcClient> | undefined;

export async function rpcClient(): Promise<NeoRpcClient> {
  defaultClient ??= connect().then(connection => new NeoRpcClient(connection));
  return defaultClient;
}

export async function invoke<TRequest, TResult>(command: string, args: TRequest, options?: NeoRpcCallOptions): Promise<TResult> {
  return (await rpcClient()).invoke<TRequest, TResult>(command, args, options);
}

export async function subscribe<T>(event: string, handler: NeoRpcEventHandler<T>, options?: NeoRpcCallOptions): Promise<NeoRpcUnsubscribe> {
  return (await rpcClient()).subscribe(event, handler, options);
}

export async function invokeChannel<TRequest, TItem>(command: string, args: TRequest, options?: NeoRpcCallOptions): Promise<AsyncIterable<TItem>> {
  const client = await rpcClient();
  const result = await client.invoke<TRequest, { readonly channel: string }>(command, args, options);
  return client.channel<TItem>(result.channel);
}

function parseError(value: unknown): NeoRpcError {
  if (!isRecord(value) || typeof value.code !== "string" || !/^[a-z][a-z0-9_]*(?::[a-z][a-z0-9_]*)*$/.test(value.code) || value.code.length > 128 ||
      typeof value.message !== "string" || value.message.trim().length === 0 || value.message.length > 512 || /[\x00-\x1f\x7f-\x9f]/.test(value.message) ||
      typeof value.retryable !== "boolean" || value.correlationId !== undefined && (typeof value.correlationId !== "string" || value.correlationId.length === 0 || value.correlationId.length > 128 || !/^[\x21-\x7e]+$/.test(value.correlationId)))
    return new NeoRpcError({ code: "invalid_request", message: "The host returned an invalid RPC error.", retryable: false });
  return new NeoRpcError({
    code: value.code,
    message: value.message,
    retryable: value.retryable === true,
    correlationId: typeof value.correlationId === "string" ? value.correlationId : undefined,
  });
}

function abortError(): NeoRpcError {
  return new NeoRpcError({ code: "operation_canceled", message: "The RPC operation was canceled.", retryable: false });
}

function normalize(value: unknown): Error {
  return value instanceof Error ? value : new NeoRpcError({ code: "connection_closed", message: "The RPC transport failed.", retryable: true });
}

function assertWireName(value: string, name: string): void {
  if (typeof value !== "string" || value.length === 0 || value.length > 192 || !/^[A-Za-z0-9_][A-Za-z0-9_.:-]*$/.test(value))
    throw new TypeError(`The ${name} wire name is invalid.`);
}

function assertContractHash(value: string | undefined): void {
  if (value !== undefined && (value.length === 0 || value.length > 256 || !/^[\x20-\x7e]+$/.test(value)))
    throw new TypeError("The contract hash must be non-empty bounded printable ASCII.");
}

function assertOpaqueId(value: string, name: string): void {
  if (typeof value !== "string" || value.length === 0 || value.length > 128 || !/^[\x21-\x7e]+$/.test(value))
    throw new TypeError(`The ${name} is invalid.`);
}

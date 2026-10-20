export const PROTOCOL_MAJOR = 1;
export const PROTOCOL_MINOR = 0;
export const SUPPORTED_FEATURES = Object.freeze(["invoke", "cancel", "events"] as const);
// Match native and managed signed 32-bit byte lengths, not an application payload quota.
export const HARD_MAXIMUM_FRAME_BYTES = 2 ** 31 - 1;
export const DEFAULT_MAXIMUM_FRAME_BYTES = HARD_MAXIMUM_FRAME_BYTES;
export const DEFAULT_MAXIMUM_JSON_DEPTH = 32;
export const DEFAULT_DIAGNOSTIC_QUEUE_LIMIT = 100;
export const DEFAULT_HANDSHAKE_TIMEOUT_MILLISECONDS = 10_000;

export type NeoAstraConnectionState =
  | "unavailable"
  | "discovering"
  | "handshaking"
  | "connected"
  | "closing"
  | "closed"
  | "failed";

export class NeoAstraClientError extends Error {
  readonly code: string;
  readonly correlationId?: string;
  readonly retryable: boolean;

  constructor(code: string, message: string, retryable = false, correlationId?: string) {
    super(message);
    this.name = "NeoAstraClientError";
    this.code = code;
    this.retryable = retryable;
    this.correlationId = correlationId;
  }
}

export function frameByteLength(value: unknown): number {
  let json: string;
  try {
    json = JSON.stringify(value);
  } catch {
    throw new NeoAstraClientError("invalid_frame", "The transport frame is not JSON serializable.");
  }
  if (json === undefined) {
    throw new NeoAstraClientError("invalid_frame", "The transport frame must be a JSON value.");
  }
  return new TextEncoder().encode(json).byteLength;
}

/** Checks a frame the page sends. The host reads the envelope around it with the same depth limit, so the frame keeps one level for it. */
export function assertOutboundFrame(value: unknown, maximumFrameBytes: number, maximumJsonDepth = DEFAULT_MAXIMUM_JSON_DEPTH): asserts value is Record<string, unknown> {
  assertFrameShape(value);
  if (exceedsJsonDepth(value, maximumJsonDepth - 1)) throw depthError();
  if (frameByteLength(value) > maximumFrameBytes) {
    throw new NeoAstraClientError("payload_too_large", "The transport frame exceeds the negotiated byte limit.");
  }
}

/** Checks a frame the host sent, whose own depth is limited by the negotiated depth. */
export function assertInboundFrame(value: unknown, maximumFrameBytes: number, maximumJsonDepth = DEFAULT_MAXIMUM_JSON_DEPTH): asserts value is Record<string, unknown> {
  assertFrameShape(value);
  if (frameByteLength(value) > maximumFrameBytes) {
    throw new NeoAstraClientError("payload_too_large", "The transport frame exceeds the negotiated byte limit.");
  }
  if (exceedsJsonDepth(value, maximumJsonDepth)) throw depthError();
}

function assertFrameShape(value: unknown): asserts value is Record<string, unknown> {
  if (!isRecord(value) || value.neoastra !== 1 || typeof value.kind !== "string" || value.kind.length === 0) {
    throw new NeoAstraClientError("invalid_frame", "A frame must be an object with the NeoAstra discriminator and a kind.");
  }
  if (value.kind === "hello" || value.kind === "hello_ack" || value.kind === "close" || value.kind === "diagnostic") {
    throw new NeoAstraClientError("invalid_frame", "Transport control frame kinds are reserved.");
  }
}

function depthError(): NeoAstraClientError {
  return new NeoAstraClientError("invalid_frame", "The transport frame exceeds the negotiated JSON depth.");
}

/**
 * Tells whether a value nests more than `maximumDepth` objects and arrays, counted as System.Text.Json counts
 * `MaxDepth` on the host: each object or array is one level, and a primitive value adds none. The walk is
 * recursive and allocates nothing; it stops at the limit, so a cyclic value is reported as too deep. An object
 * that serializes through `toJSON`, such as a `Date`, counts as one level, which can only be stricter.
 */
export function exceedsJsonDepth(value: unknown, maximumDepth: number): boolean {
  return typeof value === "object" && value !== null && nestsBeyond(value, maximumDepth);
}

function nestsBeyond(value: object, remaining: number): boolean {
  if (remaining <= 0) return true;
  if (Array.isArray(value)) {
    for (let index = 0; index < value.length; index++) {
      const item: unknown = value[index];
      if (typeof item === "object" && item !== null && nestsBeyond(item, remaining - 1)) return true;
    }
    return false;
  }
  for (const key in value) {
    const item: unknown = (value as Record<string, unknown>)[key];
    if (typeof item === "object" && item !== null && nestsBeyond(item, remaining - 1)) return true;
  }
  return false;
}

/**
 * The top-level identity of an inbound frame the client refused, given to the receive handler's rejection
 * callback so that the call, channel or subscription it answers fails instead of waiting forever.
 */
export interface NeoAstraRejectedFrame {
  readonly kind: string;
  readonly id?: string;
  readonly channel?: string;
  readonly subscription?: string;
  readonly sequence?: number;
  readonly error: NeoAstraClientError;
}

export function describeRejectedFrame(value: Record<string, unknown>, error: NeoAstraClientError): NeoAstraRejectedFrame {
  return Object.freeze({
    kind: value.kind as string,
    id: typeof value.id === "string" ? value.id : undefined,
    channel: typeof value.channel === "string" ? value.channel : undefined,
    subscription: typeof value.subscription === "string" ? value.subscription : undefined,
    sequence: Number.isSafeInteger(value.sequence) ? value.sequence as number : undefined,
    error,
  });
}

/** Reads the bounded reason token of a host close frame; anything else is reported as "unspecified". */
export function parseCloseReason(value: unknown): string {
  return typeof value === "string" && /^[a-z][a-z0-9_]{0,63}$/.test(value) ? value : "unspecified";
}

export function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

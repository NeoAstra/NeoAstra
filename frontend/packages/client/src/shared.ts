export const PROTOCOL_MAJOR = 1;
export const PROTOCOL_MINOR = 0;
export const SUPPORTED_FEATURES = Object.freeze(["invoke", "cancel", "events"] as const);
export const DEFAULT_MAXIMUM_FRAME_BYTES = 1024 * 1024;
export const HARD_MAXIMUM_FRAME_BYTES = 16 * 1024 * 1024;
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

export function assertApplicationFrame(value: unknown, maximumFrameBytes: number, maximumJsonDepth = DEFAULT_MAXIMUM_JSON_DEPTH): asserts value is Record<string, unknown> {
  if (!isRecord(value) || value.neoastra !== 1 || typeof value.kind !== "string" || value.kind.length === 0) {
    throw new NeoAstraClientError("invalid_frame", "A frame must be an object with the NeoAstra discriminator and a kind.");
  }
  if (["hello", "hello_ack", "close", "diagnostic"].includes(value.kind)) {
    throw new NeoAstraClientError("invalid_frame", "Transport control frame kinds are reserved.");
  }
  if (frameByteLength(value) > maximumFrameBytes) {
    throw new NeoAstraClientError("payload_too_large", "The transport frame exceeds the negotiated byte limit.");
  }
  const pending: Array<{ value: unknown; depth: number }> = [{ value, depth: 1 }];
  while (pending.length !== 0) {
    const current = pending.pop()!;
    if (current.depth > maximumJsonDepth) throw new NeoAstraClientError("invalid_frame", "The transport frame exceeds the negotiated JSON depth.");
    if (Array.isArray(current.value)) {
      for (const item of current.value) pending.push({ value: item, depth: current.depth + 1 });
    } else if (isRecord(current.value)) {
      for (const item of Object.values(current.value)) pending.push({ value: item, depth: current.depth + 1 });
    }
  }
}

export function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

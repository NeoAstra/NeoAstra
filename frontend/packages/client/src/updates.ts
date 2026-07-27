import type { NeoRpcCallOptions, NeoRpcUnsubscribe } from "./rpc.js";

/** Truthful updater availability. `available` is intentionally absent until artifact-specific qualification exists. */
export type NeoUpdateMode = "unavailable" | "experimental" | "store-managed";
export type NeoUpdatePhase = "idle" | "checking" | "available" | "downloading" | "ready" | "installing" | "rolled-back" | "failed";
export interface NeoUpdateStatus {
  readonly mode: NeoUpdateMode;
  readonly phase: NeoUpdatePhase;
  readonly currentVersion: string;
  readonly availableVersion?: string;
  readonly progressPercent?: number;
  readonly canInstall: boolean;
  readonly code?: string;
}
export interface NeoUpdateRpc {
  invoke<TRequest, TResult>(command: string, args: TRequest, options?: NeoRpcCallOptions): Promise<TResult>;
  subscribe<T>(event: string, handler: (value: T) => void, options?: NeoRpcCallOptions): Promise<NeoRpcUnsubscribe>;
}

export const updateCommands = Object.freeze({
  status: "updates.status",
  check: "updates.check",
  download: "updates.download",
  install: "updates.install-restart",
  changed: "updates.changed",
} as const);

/**
 * Creates the bounded renderer update surface. Feed URLs, keys, channels, versions, artifact paths,
 * helper commands, and rollback targets are deliberately not accepted from renderer code.
 */
export function createUpdateClient(rpc: NeoUpdateRpc) {
  if (rpc === null || typeof rpc !== "object" || typeof rpc.invoke !== "function" || typeof rpc.subscribe !== "function") throw new TypeError("An update RPC client is required.");
  return Object.freeze({
    status: (options?: NeoRpcCallOptions) => rpc.invoke<Record<string, never>, NeoUpdateStatus>(updateCommands.status, {}, options),
    check: (options?: NeoRpcCallOptions) => rpc.invoke<Record<string, never>, NeoUpdateStatus>(updateCommands.check, {}, options),
    download: (options?: NeoRpcCallOptions) => rpc.invoke<Record<string, never>, NeoUpdateStatus>(updateCommands.download, {}, options),
    /** Requires a separately granted high-risk backend permission and trusted user confirmation. */
    installAndRestart: (options?: NeoRpcCallOptions) => rpc.invoke<Record<string, never>, NeoUpdateStatus>(updateCommands.install, {}, options),
    onChanged: (handler: (value: NeoUpdateStatus) => void, options?: NeoRpcCallOptions) => rpc.subscribe(updateCommands.changed, handler, options),
  });
}

export type NeoUpdateClient = ReturnType<typeof createUpdateClient>;

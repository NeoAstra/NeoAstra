import {
  NeoRpcError,
  desktopCommands,
  type NeoRpcCallOptions,
} from "@neoastra/client";
import {
  createMockDesktop,
  createMockRpcHarness,
} from "@neoastra/client/testing";
import { neoRpcContractHash } from "#neoastra";
import { withAdvancedContract } from "./tour-api";

const observedContractHashes: Array<string | undefined> = [];
const contractRpc = withAdvancedContract({
  async invoke<TRequest, TResult>(_command: string, _args: TRequest, options?: NeoRpcCallOptions) {
    observedContractHashes.push(options?.contractHash);
    return undefined as TResult;
  },
  async subscribe<T>(_event: string, _handler: (value: T) => void, options?: NeoRpcCallOptions) {
    observedContractHashes.push(options?.contractHash);
    return async () => {};
  },
});
await contractRpc.invoke("desktop.test", {});
await contractRpc.subscribe("desktop.test-event", () => {});
if (observedContractHashes.some(hash => hash !== neoRpcContractHash)) {
  throw new Error("Desktop RPC calls must carry the generated host contract hash.");
}

const rpc = createMockRpcHarness();
rpc.register("tour.hello", ({ args }) => {
  const request = args as { name: string };
  return {
    message: `Hello, ${request.name}!`,
    viewLabel: "main",
  };
});

const greeting = await rpc.client.invoke<
  { name: string },
  { message: string; viewLabel: string }
>("tour.hello", { name: "advanced" });
if (greeting.message !== "Hello, advanced!" || greeting.viewLabel !== "main") {
  throw new Error("Mock typed RPC result did not match the feature-tour contract.");
}
rpc.close();

const desktop = createMockDesktop();
let denied = false;
try {
  await desktop.client.system.metadata();
} catch (error) {
  denied = error instanceof NeoRpcError && error.code === "permission_denied";
}
if (!denied) throw new Error("Desktop mock must deny commands by default.");

desktop.setResult(desktopCommands.system.metadata, {
  applicationName: "NeoAstra Advanced Sample",
  backend: "webview2",
});
const metadata = await desktop.client.system.metadata();
if ((metadata as { applicationName?: string }).applicationName !== "NeoAstra Advanced Sample") {
  throw new Error("Explicit desktop mock result was not returned.");
}
if (desktop.invocations.length !== 2) {
  throw new Error("Desktop mock did not record both denied and allowed calls.");
}

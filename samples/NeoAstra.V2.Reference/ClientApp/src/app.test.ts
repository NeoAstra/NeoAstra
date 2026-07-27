import {
  NeoRpcError,
  desktopCommands,
} from "@neoastra/client";
import {
  createMockDesktop,
  createMockRpcHarness,
} from "@neoastra/client/testing";

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
>("tour.hello", { name: "reference" });
if (greeting.message !== "Hello, reference!" || greeting.viewLabel !== "main") {
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
  applicationName: "NeoAstra v2 Feature Tour",
  backend: "webview2",
});
const metadata = await desktop.client.system.metadata();
if ((metadata as { applicationName?: string }).applicationName !== "NeoAstra v2 Feature Tour") {
  throw new Error("Explicit desktop mock result was not returned.");
}
if (desktop.invocations.length !== 2) {
  throw new Error("Desktop mock did not record both denied and allowed calls.");
}

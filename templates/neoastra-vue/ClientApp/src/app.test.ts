import { createMockRpcHarness } from "@neoastra/client/testing";

const harness = createMockRpcHarness();
harness.register("greeting.hello", ({ args }) => ({ message: `Hello, ${(args as { name: string }).name}!` }));
const result = await harness.client.invoke<{ name: string }, { message: string }>("greeting.hello", { name: "desktop" });
if (result.message !== "Hello, desktop!") throw new Error("Mock RPC result did not match.");
harness.close();

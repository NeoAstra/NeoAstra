import { createMockRpcHarness } from "@neoastra/client/testing";

const harness = createMockRpcHarness();
harness.register("notes.hello", ({ args }) => ({ message: `Hello, ${(args as { name: string }).name}!` }));
const result = await harness.client.invoke<{ name: string }, { message: string }>("notes.hello", { name: "reference" });
if (result.message !== "Hello, reference!") throw new Error("Mock RPC result did not match.");
harness.close();

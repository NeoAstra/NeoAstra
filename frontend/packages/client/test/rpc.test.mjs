import assert from "node:assert/strict";
import test from "node:test";
import { NeoRpcClient, NeoRpcError } from "../dist/index.js";
import { createMockClient, createMockRpcHarness } from "../dist/testing.js";

const tick = () => new Promise(resolve => setTimeout(resolve, 0));

test("typed RPC mock invokes, maps errors, and records protocol frames", async () => {
  let next = 0;
  const mock = createMockRpcHarness({ idFactory: () => `id-${++next}` });
  mock.register("documents.open", async ({ args }) => ({ title: args.id.toUpperCase() }));
  assert.deepEqual(await mock.client.invoke("documents.open", { id: "readme" }, { contractHash: "fixture-v1" }), { title: "README" });
  assert.equal(mock.outboundFrames[0].kind, "invoke");
  assert.equal(mock.outboundFrames[0].contract, "fixture-v1");

  await assert.rejects(
    mock.client.invoke("missing.command", {}),
    error => error instanceof NeoRpcError && error.code === "command_not_found");
  mock.close();
});

test("already aborted calls send no invoke and active abort sends cancel", async () => {
  let next = 0;
  const mock = createMockRpcHarness({ idFactory: () => `id-${++next}` });
  const already = new AbortController();
  already.abort();
  await assert.rejects(mock.client.invoke("slow", {}, { signal: already.signal }), error => error.code === "operation_canceled");
  assert.equal(mock.outboundFrames.length, 0);

  mock.register("slow", ({ signal }) => new Promise((resolve, reject) => {
    signal.addEventListener("abort", () => reject(new Error("stopped")), { once: true });
  }));
  const controller = new AbortController();
  const pending = mock.client.invoke("slow", {}, { signal: controller.signal });
  await tick();
  controller.abort();
  await assert.rejects(pending, error => error.code === "operation_canceled");
  await tick();
  assert.deepEqual(mock.outboundFrames.map(frame => frame.kind), ["invoke", "cancel"]);
  mock.close();
});

test("subscriptions preserve sequence and unsubscribe", async () => {
  let next = 0;
  const mock = createMockRpcHarness({ idFactory: () => `id-${++next}` });
  const values = [];
  const unsubscribePromise = mock.client.subscribe("documents.changed", value => values.push(value));
  await tick();
  const unsubscribe = await unsubscribePromise;
  mock.emit("documents.changed", { id: 1 });
  mock.emit("documents.changed", { id: 2 });
  assert.deepEqual(values, [{ id: 1 }, { id: 2 }]);
  await unsubscribe();
  await tick();
  mock.emit("documents.changed", { id: 3 });
  assert.equal(values.length, 2);
  assert.equal(mock.outboundFrames.at(-1).kind, "unsubscribe");
  mock.close();
});

test("pending subscription timeout unsubscribes with one terminal cleanup", async () => {
  const transport = createMockClient();
  const client = new NeoRpcClient(await transport.connect(), { idFactory: () => "timed-subscription" });
  transport.outboundFrames.length = 0;
  await assert.rejects(client.subscribe("documents.changed", () => {}, { timeoutMilliseconds: 5 }), error => error instanceof NeoRpcError && error.code === "timeout" && error.retryable);
  assert.deepEqual(transport.outboundFrames.map(frame => frame.kind), ["subscribe", "unsubscribe"]);
  transport.injectInbound({ neoastra: 1, kind: "subscribed", id: "timed-subscription" });
  await tick();
  client.close();
  assert.equal(transport.outboundFrames.filter(frame => frame.kind === "unsubscribe").length, 1);

  const invalidTransport = createMockClient();
  const invalidClient = new NeoRpcClient(await invalidTransport.connect(), { idFactory: () => "invalid-timeout" });
  invalidTransport.outboundFrames.length = 0;
  await assert.rejects(invalidClient.subscribe("documents.changed", () => {}, { timeoutMilliseconds: 0 }), TypeError);
  assert.equal(invalidTransport.outboundFrames.length, 0);
  invalidClient.close();
});

test("connection close rejects outstanding calls", async () => {
  const mock = createMockRpcHarness();
  mock.register("slow", () => new Promise(() => {}));
  const pending = mock.client.invoke("slow", {});
  await tick();
  mock.close();
  await assert.rejects(pending, error => error.code === "connection_closed");
});

test("channel results retain items and completion until the iterable is retrieved", async () => {
  const transport = createMockClient();
  const client = new NeoRpcClient(await transport.connect(), { idFactory: () => "channel-request" });
  const pending = client.invoke("documents.stream", {});
  transport.injectInbound({ neoastra: 1, kind: "result", id: "channel-request", ok: true, value: { channel: "channel-1" } });
  transport.injectInbound({ neoastra: 1, kind: "channel_item", channel: "channel-1", sequence: 1, value: "first" });
  transport.injectInbound({ neoastra: 1, kind: "channel_complete", channel: "channel-1" });
  const result = await pending;
  const iterator = client.channel(result.channel)[Symbol.asyncIterator]();
  assert.deepEqual(await iterator.next(), { done: false, value: "first" });
  assert.deepEqual(await iterator.next(), { done: true, value: undefined });
  client.close();
});

test("channel errors and connection close reject awaiting iterators", async () => {
  const transport = createMockClient();
  const client = new NeoRpcClient(await transport.connect(), { idFactory: () => "channel-request" });
  const pending = client.invoke("documents.stream", {});
  transport.injectInbound({ neoastra: 1, kind: "result", id: "channel-request", ok: true, value: { channel: "channel-2" } });
  const iterator = client.channel((await pending).channel)[Symbol.asyncIterator]();
  const waiting = iterator.next();
  transport.injectInbound({ neoastra: 1, kind: "channel_error", channel: "channel-2", error: { code: "internal_error", message: "Stream failed.", retryable: false } });
  await assert.rejects(waiting, error => error.code === "internal_error");

  const iterable = client.channel("channel-3");
  const closedWaiting = iterable[Symbol.asyncIterator]().next();
  transport.close();
  await assert.rejects(closedWaiting, error => error.code === "connection_closed");
});

test("frontend channel buffering is bounded and fails rather than dropping", async () => {
  const transport = createMockClient();
  const client = new NeoRpcClient(await transport.connect(), { idFactory: () => "channel-request", maximumBufferedChannelItems: 2 });
  const pending = client.invoke("documents.stream", {});
  transport.injectInbound({ neoastra: 1, kind: "result", id: "channel-request", ok: true, value: { channel: "channel-4" } });
  for (let sequence = 1; sequence <= 3; sequence++) transport.injectInbound({ neoastra: 1, kind: "channel_item", channel: "channel-4", sequence, value: sequence });
  const iterator = client.channel((await pending).channel)[Symbol.asyncIterator]();
  assert.equal((await iterator.next()).value, 1);
  assert.equal((await iterator.next()).value, 2);
  await assert.rejects(iterator.next(), error => error.code === "too_many_requests");
  assert.equal(transport.outboundFrames.some(frame => frame.kind === "channel_close"), true);
  client.close();
});

test("result and AbortSignal races have one deterministic client-side winner", async () => {
  const transport = createMockClient();
  const client = new NeoRpcClient(await transport.connect(), { idFactory: (() => { let id = 0; return () => `race-${++id}`; })() });
  const lateAbort = new AbortController();
  const committed = client.invoke("documents.open", {}, { signal: lateAbort.signal });
  transport.injectInbound({ neoastra: 1, kind: "result", id: "race-1", ok: true, value: 42 });
  lateAbort.abort();
  assert.equal(await committed, 42);

  const earlyAbort = new AbortController();
  const canceled = client.invoke("documents.open", {}, { signal: earlyAbort.signal });
  earlyAbort.abort();
  transport.injectInbound({ neoastra: 1, kind: "result", id: "race-2", ok: true, value: 43 });
  await assert.rejects(canceled, error => error.code === "operation_canceled");
  assert.equal(transport.outboundFrames.filter(frame => frame.kind === "cancel").length, 1);
  client.close();
});

test("constructor contains a connection-close race and detaches its receive handler", () => {
  const closed = new AbortController();
  let removed = 0;
  let state = "connected";
  const connection = {
    runtimeInfo: {},
    get state() { return state; },
    closed: closed.signal,
    hasFeature: () => false,
    send() { },
    setReceiveHandler() {
      state = "closed";
      closed.abort();
      return () => { removed++; };
    },
    close() { },
  };
  assert.throws(() => new NeoRpcClient(connection), error => error.code === "connection_closed");
  assert.equal(removed, 1);
});

test("malformed host errors are replaced by a bounded stable client error", async () => {
  const transport = createMockClient();
  let id = 0;
  const client = new NeoRpcClient(await transport.connect(), { idFactory: () => `error-${++id}` });
  for (const error of [
    { code: "Bad.Code", message: "unsafe", retryable: false },
    { code: "safe_code", message: "line\nbreak", retryable: false },
    { code: "safe_code", message: "control\u0085character", retryable: false },
    { code: "safe_code", message: "safe", retryable: false, correlationId: "bad\nvalue" },
    { code: "safe_code", message: "safe", retryable: "yes" },
  ]) {
    const pending = client.invoke("errors.test", {});
    transport.injectInbound({ neoastra: 1, kind: "result", id: `error-${id}`, ok: false, error });
    await assert.rejects(pending, value => value instanceof NeoRpcError && value.code === "invalid_request" && value.message === "The host returned an invalid RPC error.");
  }
  client.close();
});

test("client close settles and clears every owned state exactly once", async () => {
  const transport = createMockClient();
  let id = 0;
  const client = new NeoRpcClient(await transport.connect(), { idFactory: () => `close-${++id}` });
  const call = client.invoke("slow.call", {});
  const subscription = client.subscribe("slow.event", () => {});
  const channel = client.channel("owned-channel")[Symbol.asyncIterator]().next();
  client.close();
  await assert.rejects(call, error => error.code === "connection_closed");
  await assert.rejects(subscription, error => error.code === "connection_closed");
  await assert.rejects(channel, error => error.code === "connection_closed");
  const terminalFrames = transport.outboundFrames.filter(frame => frame.kind === "cancel" || frame.kind === "unsubscribe" || frame.kind === "channel_close").length;
  transport.close();
  client.close();
  assert.equal(transport.outboundFrames.filter(frame => frame.kind === "cancel" || frame.kind === "unsubscribe" || frame.kind === "channel_close").length, terminalFrames);
});

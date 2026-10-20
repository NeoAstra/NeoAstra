import { connect } from "./neoastra-client/index.js";

let connection;
const ready = connect().then(value => { connection = value; globalThis.__benchmarkTransportConnected = true; });

globalThis.__benchmarkSend = (token, count, payloadSize) => {
  if (!connection) throw new Error("The NeoAstra client handshake is not complete.");
  const payload = "x".repeat(payloadSize);
  for (let index = 0; index < count; index++) {
    connection.send({ neoastra: 1, kind: "benchmark", token, index, payload });
  }
  return count;
};

// RPC frames from the host: the page counts events of its subscription and results of its calls, and tells the host
// with a frame of its own when it received what it expects.
let expected = 0;
let received = 0;
let nextId = 0;
const expect = count => { expected = count; received = 0; };
const receivedOne = () => { if (++received === expected) connection.send({ neoastra: 1, kind: "benchmark_done" }); };
globalThis.__benchmarkSubscribe = () => {
  connection.setReceiveHandler(frame => {
    if (frame.kind === "subscribed") globalThis.__benchmarkSubscribed = frame.error === undefined;
    else if (frame.kind === "event" || frame.kind === "result") receivedOne();
  });
  connection.send({ neoastra: 1, kind: "subscribe", id: "benchmark-events", event: "benchmark.changed" });
};
globalThis.__benchmarkExpectEvents = count => { expect(count); return count; };
globalThis.__benchmarkFetch = count => {
  expect(count);
  for (let index = 0; index < count; index++) connection.send({ neoastra: 1, kind: "invoke", id: `fetch-${++nextId}`, command: "benchmark.blob", args: index });
  return count;
};

globalThis.__benchmarkTransportReady = ready;

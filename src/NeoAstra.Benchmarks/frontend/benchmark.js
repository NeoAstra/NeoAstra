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

globalThis.__benchmarkTransportReady = ready;

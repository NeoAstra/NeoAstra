import { connect } from "./neoastra-client/index.js";

globalThis.__fixtureExternalAsset = "loaded";
globalThis.__fixtureSawDocumentStartScript = globalThis.__neoDocumentStart === "injected";
globalThis.__fixtureHostMessages = [];

let connection;
const pending = [];
globalThis.__fixturePostMessage = value => {
  const frame = { neoastra: 1, ...value };
  if (connection) connection.send(frame);
  else pending.push(frame);
};

globalThis.__fixtureTransportReady = connect().then(value => {
  connection = value;
  connection.setReceiveHandler(frame => globalThis.__fixtureHostMessages.push(frame));
  for (const frame of pending.splice(0)) connection.send(frame);
  globalThis.__fixtureTransportConnected = true;
});

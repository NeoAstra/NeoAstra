import { connect, onDiagnostic } from "./neoastra-client/index.js";

const output = document.querySelector("#received");
onDiagnostic(value => { if (value.level === "error") output.textContent = `${value.code}: ${value.message}`; });

const connection = await connect();
connection.setReceiveHandler(value => { output.textContent = JSON.stringify(value, null, 2); });
const send = () => connection.send({ neoastra: 1, kind: "sample", message: "Hello from app://neoastra" });
document.querySelector("#send").addEventListener("click", send);
send();

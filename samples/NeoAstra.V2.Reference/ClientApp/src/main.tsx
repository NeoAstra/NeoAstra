import React from "react";
import { createRoot } from "react-dom/client";
import { notes } from "./generated/neoastra";
import markUrl from "./reference-mark.svg";
import { installNavigationGuard } from "./security";
import "./style.css";

installNavigationGuard();

const worker = new Worker(new URL("./reference.worker.ts", import.meta.url), { type: "module" });
worker.postMessage("ready");

function App() {
  const [message, setMessage] = React.useState("");
  const [workerMessage, setWorkerMessage] = React.useState("Waiting for module worker.");
  const [details, setDetails] = React.useState("Loading dynamic reference chunk.");
  React.useEffect(() => {
    worker.onmessage = (event: MessageEvent<string>) => setWorkerMessage(event.data);
    void import("./details").then((module) => setDetails(module.referenceDetails()));
    return () => worker.terminate();
  }, []);
  return <main><img className="mark" src={markUrl} alt=""/><h1>NeoAstra v2 Reference</h1><p>{details}</p><p>{workerMessage}</p><button type="button" onClick={async () => setMessage((await notes.hello({ name: "desktop" })).message)}>Typed RPC</button><output aria-live="polite">{message}</output></main>;
}
createRoot(document.querySelector("#app")!).render(<App />);

import { invoke, rpcClient, getRuntimeInfo, createDesktopClient } from "./client/index.js";

const features = document.querySelector("#features");
const encode = text => btoa(String.fromCharCode(...new TextEncoder().encode(text)));
const decode = base64 => new TextDecoder().decode(Uint8Array.from(atob(base64), c => c.charCodeAt(0)));

// One card per capability: its controls, the status of the last call, and what the host answered.
function card(title, build) {
  const element = document.createElement("section");
  element.className = "card";
  element.innerHTML = `<h2><span>${title}</span><span class="badge">…</span></h2><div class="row"></div><pre></pre>`;
  const badge = element.querySelector(".badge"), output = element.querySelector("pre");
  const report = (status, value) => { badge.textContent = status; badge.className = `badge ${status}`; output.textContent = typeof value === "string" ? value : JSON.stringify(value, null, 1); };
  const run = async action => {
    try { const value = await action(); report(value?.status ?? "Success", value); return value; }
    catch (error) { report("error", `${error.code ?? ""} ${error.message}`.trim()); }
  };
  build(element.querySelector(".row"), run);
  features.append(element);
}
const button = (row, text, action) => { const value = document.createElement("button"); value.textContent = text; value.onclick = action; row.append(value); };
const input = (row, value) => { const element = document.createElement("input"); element.value = value; row.append(element); return element; };

const desktop = createDesktopClient(await rpcClient());

card("Greeting (generated RPC)", (row, run) => {
  const name = input(row, "NeoAstra");
  button(row, "Greet", () => run(() => invoke("greeting.hello", { name: name.value })));
  run(() => invoke("greeting.hello", { name: name.value }));
});
card("System theme", (row, run) => {
  button(row, "Read", () => run(() => desktop.system.theme()));
  run(() => desktop.system.theme());
});
card("Application metadata", (row, run) => {
  button(row, "Read", () => run(() => desktop.system.metadata()));
});
card("Clipboard", (row, run) => {
  const text = input(row, "Hello from NeoAstra");
  button(row, "Copy", () => run(() => desktop.clipboard.write("text", encode(text.value))));
  button(row, "Paste", () => run(async () => { const value = await desktop.clipboard.read("text"); return value.base64 ? { ...value, text: decode(value.base64) } : value; }));
});
card("Safe storage", (row, run) => {
  const secret = input(row, "a secret");
  button(row, "Store", () => run(() => desktop.safeStorage.store("hello-secret", encode(secret.value))));
  button(row, "Read", () => run(async () => { const value = await desktop.safeStorage.retrieve("hello-secret"); return value.base64 ? { ...value, text: decode(value.base64) } : value; }));
  button(row, "Delete", () => run(() => desktop.safeStorage.delete("hello-secret")));
});
card("Open a link", (row, run) => {
  button(row, "github.com/NeoAstra", () => run(() => desktop.opener.url("https://github.com/NeoAstra")));
});
card("Notification", (row, run) => {
  button(row, "Status", () => run(() => desktop.notifications.status()));
  button(row, "Show", () => run(() => desktop.notifications.show({ appIdentity: "dev.neoastra.sample.mobile", category: "general", urgency: "normal", persistent: false, id: "hello", title: "Hello NeoAstra", body: "A notification from the page.", actions: [] })));
});
card("Message dialog", (row, run) => {
  button(row, "Show", () => run(() => desktop.dialogs.message({ title: "Hello", message: "A native dialog from the page. Which button do you choose?", icon: "Question", buttons: ["Accept", "Cancel"] })));
});
card("Window title (a desktop window)", (row, run) => {
  button(row, "Set title", () => run(() => desktop.window.setTitle("Hello")));
});
card("Tray icon (a desktop tray)", (row, run) => {
  button(row, "Create", () => run(() => desktop.tray.create({ id: "hello", toolTip: "Hello", isTemplateImage: false, menu: [] })));
});

const runtime = getRuntimeInfo();
document.querySelector("#runtime").textContent = `${runtime.platform} · ${runtime.backend} · protocol ${runtime.protocolMajor}.${runtime.protocolMinor}`;

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
card("Web page (browser features)", (row, run) => {
  button(row, "alert", () => run(() => { alert("An alert of the page."); return { status: "Success", alert: "closed" }; }));
  button(row, "confirm", () => run(() => ({ status: "Success", confirmed: confirm("Do you confirm?") })));
  const file = document.createElement("input");
  file.type = "file";
  file.onchange = () => run(() => ({ status: file.files.length ? "Success" : "Canceled", files: [...file.files].map(value => `${value.name} (${value.size} bytes)`) }));
  row.append(file);
  button(row, "Camera", () => run(async () => {
    const stream = await navigator.mediaDevices.getUserMedia({ video: true });
    const tracks = stream.getVideoTracks().map(track => track.label);
    stream.getTracks().forEach(track => track.stop());
    return { status: "Success", tracks };
  }));
});
card("Haptics", (row, run) => {
  for (const feedback of ["Light", "Medium", "Heavy", "Selection", "Success", "Warning", "Error"]) button(row, feedback, () => run(() => desktop.haptics.play(feedback)));
});
card("Location", (row, run) => {
  const fix = { accuracy: "High", timeoutMilliseconds: 20000 };
  button(row, "Status", () => run(() => invoke("location.authorization", {})));
  button(row, "Ask", () => run(() => invoke("location.request-authorization", {})));
  button(row, "Where am I?", () => run(() => invoke("location.current-position", fix)));
});
card("Push", (row, run) => {
  const rpc = rpcClient();
  button(row, "Register", () => run(() => invoke("push.register", {})));
  button(row, "Launch message", () => run(() => invoke("push.launch-message", {})));
  rpc.then(client => {
    client.subscribe("push.message-received", message => run(async () => ({ status: "Received", ...message })));
    client.subscribe("push.message-opened", message => run(async () => ({ status: "Opened", ...message })));
    client.subscribe("push.token-changed", registration => run(async () => ({ status: "Token", ...registration })));
  });
});
// A passkey belongs to a relying party whose domain lists this application; the page creates the options that its server
// would give, so the answer is not verified here.
card("Passkeys", (row, run) => {
  const bytes = length => base64url(crypto.getRandomValues(new Uint8Array(length)));
  const base64url = data => btoa(String.fromCharCode(...data)).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
  const rp = input(row, localStorage.getItem("passkeys.rp") ?? "");
  rp.placeholder = "relying party domain";
  rp.onchange = () => localStorage.setItem("passkeys.rp", rp.value);
  button(row, "Create", () => run(async () => {
    const options = { rp: { id: rp.value, name: "NeoAstra sample" }, user: { id: bytes(16), name: "neoastra-sample", displayName: "NeoAstra sample" }, challenge: bytes(32),
      pubKeyCredParams: [{ type: "public-key", alg: -7 }, { type: "public-key", alg: -257 }], authenticatorSelection: { residentKey: "required", userVerification: "preferred" }, attestation: "none" };
    const result = await invoke("passkeys.create", { options });
    if (result.value) localStorage.setItem("passkeys.id", result.value.id);
    return result;
  }));
  button(row, "Sign in", () => run(() => {
    const id = localStorage.getItem("passkeys.id");
    return invoke("passkeys.get", { options: { rpId: rp.value, challenge: bytes(32), userVerification: "preferred", allowCredentials: id ? [{ type: "public-key", id }] : [] } });
  }));
});
// The products are those of the store of the application, whose identifiers the field takes.
card("Purchases", (row, run) => {
  const id = input(row, localStorage.getItem("purchases.id") ?? "");
  id.placeholder = "product identifier";
  id.onchange = () => localStorage.setItem("purchases.id", id.value);
  const type = document.createElement("select");
  type.innerHTML = `<option>InApp</option><option>Subscription</option>`;
  type.value = localStorage.getItem("purchases.type") ?? "InApp";
  type.onchange = () => localStorage.setItem("purchases.type", type.value);
  row.append(type);
  let last;
  button(row, "Load", () => run(() => invoke("purchases.products", { products: [{ id: id.value, type: type.value }] })));
  button(row, "Buy", () => run(async () => { const result = await invoke("purchases.purchase", { id: id.value, type: type.value }); last = result.value ?? last; return result; }));
  button(row, "Owned", () => run(async () => { const result = await invoke("purchases.list", {}); last = result.value?.[0] ?? last; return result; }));
  button(row, "Restore", () => run(() => invoke("purchases.restore", {})));
  button(row, "Finish", () => run(() => last ? invoke("purchases.finish", { purchase: last, consume: type.value === "InApp" }) : { status: "NotFound" }));
  rpcClient().then(client => client.subscribe("purchases.updated", purchase => run(async () => ({ status: "Updated", ...purchase }))));
});
card("Window title", (row, run) => {
  button(row, "Set title", () => run(() => desktop.window.setTitle("Hello")));
});
card("Tray icon (a desktop tray)", (row, run) => {
  button(row, "Create", () => run(() => desktop.tray.create({ id: "hello", toolTip: "Hello", isTemplateImage: false, menu: [] })));
});

const runtime = getRuntimeInfo();
document.querySelector("#runtime").textContent = `${runtime.platform} · ${runtime.backend} · protocol ${runtime.protocolMajor}.${runtime.protocolMinor}`;

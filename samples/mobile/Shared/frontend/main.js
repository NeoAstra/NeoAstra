import { invoke, rpcClient, getRuntimeInfo, createDesktopClient, onKeyboard, keyboardHeight } from "./client/index.js";

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
card("Displays", (row, run) => {
  button(row, "Read", () => run(() => desktop.system.displays()));
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
  // Silent messages need no permission: the question is left to a later action of the user.
  button(row, "Register silently", () => run(() => invoke("push.register", { ask: false })));
  // A message that arrives while the page is shown waits for its decision, the first answer decides.
  const show = document.createElement("input");
  show.type = "checkbox";
  show.checked = true;
  show.title = "Show messages in the foreground";
  row.append(show);
  button(row, "Launch message", () => run(() => invoke("push.launch-message", {})));
  rpc.then(client => {
    client.subscribe("push.message-received", message => run(async () => ({ status: "Received", ...message })));
    client.subscribe("push.message-opened", message => run(async () => ({ status: "Opened", ...message })));
    client.subscribe("push.token-changed", registration => run(async () => ({ status: "Token", ...registration })));
    client.subscribe("push.foreground-message", ({ decisionId, message }) => run(async () => {
      await invoke("push.present", { decisionId, show: show.checked });
      return { status: show.checked ? "Shown" : "Kept", ...message };
    }));
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
// Android reports its back button as a request to close the window: the page steps back out of what it shows first, and
// only lets the window close when there is nothing left.
card("Back button (Android)", (row, run) => {
  let layers = 0;
  const report = () => ({ status: "Success", layers });
  button(row, "Open a layer", () => run(async () => { layers++; return report(); }));
  desktop.window.onCloseRequested(event => {
    if (event.reason !== "User" || !event.canCancel || layers === 0) return;
    layers--;
    event.preventDefault();
    run(async () => ({ ...report(), back: "stepped back" }));
  }).catch(error => run(() => { throw error; }));
});
// The window shows its color before and behind the page; on Android the symbols of the system bars follow it.
card("Window color", (row, run) => {
  const apply = (page, color) => run(async () => { document.documentElement.style.background = page; return desktop.window.setBackgroundColor(color); });
  button(row, "Light", () => apply("#f2f2f7", "#f2f2f7"));
  button(row, "Dark", () => apply("#1c1c1e", "#1c1c1e"));
});
// A scheduled notification outlives the page and the application; a tap on it reaches the page that listens, or waits for
// the page of the next start.
card("Reminder", (row, run) => {
  const reminder = () => ({ appIdentity: "dev.neoastra.sample.mobile", category: "reminders", urgency: "normal", persistent: false, id: "reminder.sample",
    title: "Reminder", body: "Scheduled ten seconds ago by the page.", payload: "sample:reminder", actions: [], deliverAt: new Date(Date.now() + 10000).toISOString() });
  button(row, "Ask", () => run(() => desktop.notifications.requestPermission()));
  button(row, "In 10 s", () => run(() => desktop.notifications.schedule(reminder())));
  button(row, "Cancel", () => run(() => desktop.notifications.unschedule("reminder.sample")));
  button(row, "Waiting tap", () => run(async () => ({ status: "Success", activation: await desktop.notifications.takeLaunchActivation() ?? null })));
  desktop.notifications.onActivated(activation => run(async () => ({ status: "Activated", ...activation })));
  run(async () => { const activation = await desktop.notifications.takeLaunchActivation(); return activation ? { status: "Launched", ...activation } : { status: "Success", activation: null }; });
});
// A link leaves the application only when the user tapped it (MobileHost decides as NeoApp does): the tapped link opens GitHub in
// the browser, the same link clicked by a script a few seconds after a tap opens nothing. The host tells what it saw.
card("Link: tap versus script", (row, run) => {
  const link = document.createElement("a");
  link.href = "https://github.com/NeoAstra/NeoAstra";
  link.textContent = "Tap: opens GitHub";
  row.append(link);
  // The badge says whether the host decided as expected: OpenExternal for the tap, Cancel for the script.
  const decision = expected => run(async () => { const last = await invoke("links.last", {}); return { status: last.decision === expected ? "Success" : "Failed", expected, ...last }; });
  const wait = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));
  link.addEventListener("click", event => { if (event.isTrusted) setTimeout(() => decision("OpenExternal"), 1000); });
  button(row, "Script clicks it in 6 s: opens nothing", () => run(async () => {
    await wait(6000);
    link.click();
    await wait(1000);
    return decision("Cancel");
  }));
});
// HTML on the clipboard: copied here, or copied in Safari or Chrome, which may keep it as data rather than as text.
card("Clipboard HTML", (row, run) => {
  button(row, "Copy HTML", () => run(() => desktop.clipboard.write("html", encode("<b>NeoAstra</b> <i>rich text</i>"))));
  button(row, "Paste HTML", () => run(async () => { const value = await desktop.clipboard.read("html"); return value.base64 ? { ...value, html: decode(value.base64) } : value; }));
});
// The view refuses to walk back to a document it left (HistoryNavigation off): on the second page, history.back() stays there.
card("History off", (row, run) => {
  button(row, "Open a second page", () => { location.href = "second.html"; });
  run(async () => ({ status: "Success", historyLength: history.length }));
});
// Two notifications whose IDs have the same hash code in Java ("FB" and "Ea"): each tap brings its own payload, which the
// Reminder card reports as "Activated".
card("Notifications FB and Ea", (row, run) => {
  const show = id => desktop.notifications.show({ appIdentity: "dev.neoastra.sample.mobile", category: "general", urgency: "normal", persistent: false,
    id, title: `Notification ${id}`, body: `Tap me: the page must report the payload "${id}".`, payload: id, actions: [] });
  button(row, "Show both", () => run(async () => ({ status: "Success", FB: (await show("FB")).status, Ea: (await show("Ea")).status })));
});
// The keyboard lies over the page (NeoAstraIOSKeyboardOverlay, NeoAstraAndroidKeyboardOverlay): the page learns what it covers.
card("Keyboard", (row, run) => {
  const field = input(row, "");
  field.placeholder = "Tap to open the keyboard";
  // The page keeps the field clear of the keyboard itself, with the room that the body leaves below.
  onKeyboard(state => { if (state.height > 0 && document.activeElement === field) setTimeout(() => field.scrollIntoView({ block: "center", behavior: "smooth" }), state.duration * 1000); });
  onKeyboard(state => run(async () => ({ status: "Success", ...state, variable: keyboardHeight() })));
});
// The host captures the view of the page, as an automation tool or an agent would see it, and the card shows the image.
card("Screenshot", (row, run) => {
  const image = document.createElement("img");
  image.alt = "Screenshot of this page";
  image.style.cssText = "display: none; width: 50%; margin-top: 0.5rem; border: 1px solid #8884; border-radius: 8px;";
  const holder = document.createElement("div");
  holder.style.flexBasis = "100%";
  holder.append(image);
  const show = async region => {
    const shot = await invoke("screenshot.capture", region);
    image.src = `data:image/png;base64,${shot.png}`;
    image.style.display = "block";
    return { status: "Success", width: shot.width, height: shot.height, devicePixelRatio, bytes: Math.round(shot.png.length * 3 / 4) };
  };
  button(row, "Capture", () => run(() => show({})));
  // The region of this card, in CSS pixels: the image has the size of the card times the device pixel ratio.
  button(row, "This card", () => run(() => {
    const box = row.parentElement.getBoundingClientRect();
    return show({ x: Math.round(box.x), y: Math.round(box.y), width: Math.round(box.width), height: Math.round(box.height) });
  }));
  row.append(holder);
});
card("Window title", (row, run) => {
  button(row, "Set title", () => run(() => desktop.window.setTitle("Hello")));
});
card("Tray icon (a desktop tray)", (row, run) => {
  button(row, "Create", () => run(() => desktop.tray.create({ id: "hello", toolTip: "Hello", isTemplateImage: false, menu: [] })));
});

const runtime = getRuntimeInfo();
document.querySelector("#runtime").textContent = `${runtime.platform} · ${runtime.backend} · protocol ${runtime.protocolMajor}.${runtime.protocolMinor}`;

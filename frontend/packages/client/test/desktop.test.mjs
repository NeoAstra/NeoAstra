import assert from "node:assert/strict";
import test from "node:test";
import { desktopCommands } from "../dist/index.js";
import { createMockDesktop } from "../dist/testing.js";

test("desktop clients use the static command contract and typed intent fields", async () => {
  const mock = createMockDesktop();
  mock.setResult(desktopCommands.opener.file, { status: "Success" });
  mock.setResult(desktopCommands.clipboard.clear, { status: "Success" });

  assert.deepEqual(await mock.client.opener.file({ root: "documents", relativePath: "report.pdf" }, "OpenDocument"), { status: "Success" });
  await mock.client.clipboard.clear();
  assert.deepEqual(mock.invocations, [
    { command: "desktop.opener.file", args: { root: "documents", relativePath: "report.pdf", operation: "open", intent: "OpenDocument" } },
    { command: "desktop.clipboard.clear", args: { format: "all", operation: "write" } },
  ]);
});

test("desktop mock is grant-free and contains event listener exceptions", async () => {
  const mock = createMockDesktop();
  await assert.rejects(() => mock.client.safeStorage.retrieve("missing"), error => error.code === "permission_denied");
  let received;
  await mock.client.tray.onActivated(() => { throw new Error("contained"); });
  const unsubscribe = await mock.client.tray.onActivated(value => { received = value; });
  mock.emit(desktopCommands.tray.activated, { id: "main" });
  assert.deepEqual(received, { id: "main" });
  await unsubscribe();
  received = undefined;
  mock.emit(desktopCommands.tray.activated, { id: "other" });
  assert.equal(received, undefined);
});

test("window extras use scoped files and focused typed payloads", async () => {
  const mock = createMockDesktop();
  for (const command of Object.values(desktopCommands.window)) mock.setResult(command, { status: "Success" });
  const support = { progress: { supportLevel: "Native", details: "Native taskbar progress." } };
  mock.setResult(desktopCommands.window.getExtraSupport, support);
  assert.deepEqual(await mock.client.window.extraSupport(), support);
  await mock.client.window.setIcon({ root: "assets", relativePath: "app.ico" });
  await mock.client.window.setRepresentedFile();
  await mock.client.window.setProgress("Paused", 0.5);
  await mock.client.window.setContentProtection(true);
  await mock.client.window.setTitleBarTheme("Dark");
  assert.deepEqual(mock.invocations, [
    { command: "desktop.window.get-extra-support", args: {} },
    { command: "desktop.window.set-icon", args: { root: "assets", relativePath: "app.ico", operation: "read" } },
    { command: "desktop.window.set-represented-file", args: { operation: "read" } },
    { command: "desktop.window.set-progress", args: { state: "Paused", value: 0.5 } },
    { command: "desktop.window.set-content-protection", args: { value: true } },
    { command: "desktop.window.set-titlebar-theme", args: { theme: "Dark" } },
  ]);
});

test("window management exposes state and completes close negotiation", async () => {
  const mock = createMockDesktop();
  mock.setResult(desktopCommands.window.getState, { value: { visible: true, state: "Normal" } });
  mock.setResult(desktopCommands.window.hide, { status: "Success" });
  mock.setResult(desktopCommands.window.interceptClose, { status: "Success" });
  mock.setResult(desktopCommands.window.completeClose, { status: "Success" });
  const state = await mock.client.window.state();
  await mock.client.window.hide();
  let secondListenerCalled = false;
  const [unsubscribe, unsubscribeSecond] = await Promise.all([
    mock.client.window.onCloseRequested(event => {
      assert.equal(event.reason, "User");
      event.preventDefault();
    }),
    mock.client.window.onCloseRequested(async event => {
      await Promise.resolve();
      secondListenerCalled = event.canCancel;
    }),
  ]);
  mock.emit(desktopCommands.window.closeRequested, { requestId: 7, reason: "User", canCancel: true });
  await new Promise(resolve => setTimeout(resolve, 0));
  await unsubscribe();
  await unsubscribeSecond();

  assert.equal(state.value.visible, true);
  assert.equal(secondListenerCalled, true);
  assert.deepEqual(mock.invocations, [
    { command: "desktop.window.get-state", args: {} },
    { command: "desktop.window.hide", args: {} },
    { command: "desktop.window.intercept-close", args: { value: true } },
    { command: "desktop.window.complete-close", args: { requestId: 7, preventDefault: true } },
    { command: "desktop.window.intercept-close", args: { value: false } },
  ]);
});

test("application quit uses the negotiated renderer command", async () => {
  const mock = createMockDesktop();
  mock.setResult(desktopCommands.application.requestQuit, { status: "Canceled" });

  assert.deepEqual(await mock.client.application.requestQuit(), { status: "Canceled" });
  assert.deepEqual(mock.invocations, [
    { command: "desktop.application.request-quit", args: {} },
  ]);
});

test("renderer outbound drag uses host-native gesture authority without a renderer token", async () => {
  const mock = createMockDesktop();
  mock.setResult(desktopCommands.dragDrop.outbound, { status: "Success" });
  const items = [{ kind: "Text", value: "drag me" }];

  assert.deepEqual(await mock.client.dragDrop.outbound("main", items), { status: "Success" });
  assert.deepEqual(mock.invocations, [
    { command: "desktop.drag-drop.outbound", args: { viewLabel: "main", items } },
  ]);
  assert.equal("gestureToken" in mock.invocations[0].args, false);
});

function createTitleBarDocument(nativeAppRegion) {
  const properties = new Map();
  const listeners = new Map();
  class FakeElement {
    constructor(matches, parent) { this.matches = matches; this.parent = parent; }
    closest(selector) {
      for (let node = this; node !== undefined; node = node.parent) {
        if (selector.split(",").some(part => node.matches.includes(part))) return node;
      }
      return null;
    }
    contains(other) {
      for (let node = other; node !== undefined; node = node.parent) if (node === this) return true;
      return false;
    }
  }
  class FakeStyleSheet { replaceSync(rules) { this.rules = rules; } }
  const document = {
    adoptedStyleSheets: [],
    defaultView: { Element: FakeElement, CSSStyleSheet: FakeStyleSheet, CSS: { supports: () => nativeAppRegion } },
    addEventListener: (type, listener) => listeners.set(type, listener),
    removeEventListener: (type, listener) => { if (listeners.get(type) === listener) listeners.delete(type); },
  };
  const root = {
    ownerDocument: document,
    dataset: {},
    style: { setProperty: (name, value) => properties.set(name, value), removeProperty: name => properties.delete(name) },
  };
  return { document, root, properties, listeners, FakeElement };
}

function titleBarSnapshot(overrides = {}) {
  return {
    state: "Normal", focused: true, resizable: true,
    titleBar: { style: "Overlay", height: 32, leftInset: 0, rightInset: 138 },
    ...overrides,
  };
}

test("title-bar commands send typed payloads", async () => {
  const mock = createMockDesktop();
  for (const command of Object.values(desktopCommands.window)) mock.setResult(command, { status: "Success" });
  await mock.client.window.setTitleBar({ style: "Overlay", height: 40, symbolColor: "#ffffff" });
  await mock.client.window.setTitleBar({ style: "Default" });
  await mock.client.window.startDrag();
  await mock.client.window.startResize("BottomRight");
  assert.deepEqual(mock.invocations, [
    { command: "desktop.window.set-title-bar", args: { style: "Overlay", height: 40, symbolColor: "#ffffff", backgroundColor: undefined } },
    { command: "desktop.window.set-title-bar", args: { style: "Default", height: 0, symbolColor: undefined, backgroundColor: undefined } },
    { command: "desktop.window.start-drag", args: {} },
    { command: "desktop.window.start-resize", args: { edge: "BottomRight" } },
  ]);
});

test("attached title bars publish layout and use native drag regions when the engine has them", async () => {
  const mock = createMockDesktop();
  const fake = createTitleBarDocument(true);
  mock.setResult(desktopCommands.window.getState, { value: titleBarSnapshot() });
  const changes = [];
  const binding = await mock.client.window.attachTitleBar({ root: fake.root, onChange: value => changes.push(value.state) });

  assert.equal(fake.root.dataset.neoastraTitlebar, "overlay");
  assert.equal(fake.root.dataset.neoastraWindowState, "normal");
  assert.equal(fake.root.dataset.neoastraWindowFocused, "true");
  assert.equal(fake.properties.get("--neoastra-titlebar-height"), "32px");
  assert.equal(fake.properties.get("--neoastra-titlebar-inset-left"), "0px");
  assert.equal(fake.properties.get("--neoastra-titlebar-inset-right"), "138px");
  assert.equal(fake.document.adoptedStyleSheets.length, 1);
  assert.match(fake.document.adoptedStyleSheets[0].rules, /app-region:drag/);
  assert.equal(fake.listeners.size, 0);

  mock.emit(desktopCommands.window.stateChanged, { value: titleBarSnapshot({ state: "Fullscreen", focused: false, titleBar: { style: "Overlay", height: 32, leftInset: 0, rightInset: 0 } }) });
  assert.equal(binding.snapshot.state, "Fullscreen");
  assert.equal(fake.root.dataset.neoastraWindowState, "fullscreen");
  assert.equal(fake.root.dataset.neoastraWindowFocused, "false");
  assert.equal(fake.properties.get("--neoastra-titlebar-inset-right"), "0px");
  assert.deepEqual(changes, ["Normal", "Fullscreen"]);

  await binding.dispose();
  await binding.dispose();
  assert.equal(fake.document.adoptedStyleSheets.length, 0);
  assert.deepEqual(fake.root.dataset, {});
  assert.equal(fake.properties.size, 0);
  mock.emit(desktopCommands.window.stateChanged, { value: titleBarSnapshot({ state: "Maximized" }) });
  assert.equal(binding.snapshot.state, "Fullscreen");
});

test("attached title bars start host drags on engines without drag-region styling", async () => {
  const mock = createMockDesktop();
  const fake = createTitleBarDocument(false);
  for (const command of [desktopCommands.window.startDrag, desktopCommands.window.maximize, desktopCommands.window.restore]) mock.setResult(command, { status: "Success" });
  mock.setResult(desktopCommands.window.getState, { value: titleBarSnapshot() });
  const binding = await mock.client.window.attachTitleBar({ root: fake.root });
  const press = fake.listeners.get("mousedown");
  assert.equal(typeof press, "function");
  assert.equal(fake.document.adoptedStyleSheets.length, 0);

  const region = new fake.FakeElement(["[data-neoastra-drag-region]"]);
  const label = new fake.FakeElement([], region);
  const button = new fake.FakeElement(["button"], region);
  const outside = new fake.FakeElement([]);
  let prevented = 0;
  const event = (target, detail = 1, button = 0) => ({ target, detail, button, preventDefault: () => { prevented++; } });

  press(event(outside));
  press(event(button));
  press(event(label, 1, 2));
  assert.equal(prevented, 0);
  press(event(label));
  press(event(label, 2));
  mock.emit(desktopCommands.window.stateChanged, { value: titleBarSnapshot({ state: "Maximized" }) });
  press(event(region, 2));
  mock.emit(desktopCommands.window.stateChanged, { value: titleBarSnapshot({ state: "Fullscreen" }) });
  press(event(region, 2));
  await new Promise(resolve => setTimeout(resolve, 0));

  assert.equal(prevented, 4);
  assert.deepEqual(mock.invocations.slice(1), [
    { command: "desktop.window.start-drag", args: {} },
    { command: "desktop.window.maximize", args: {} },
    { command: "desktop.window.restore", args: {} },
  ]);
  await binding.dispose();
  assert.equal(fake.listeners.size, 0);
});

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

test("window polish uses scoped files and focused typed payloads", async () => {
  const mock = createMockDesktop();
  for (const command of Object.values(desktopCommands.window)) mock.setResult(command, { status: "Success" });
  await mock.client.window.setIcon({ root: "assets", relativePath: "app.ico" });
  await mock.client.window.setRepresentedFile();
  await mock.client.window.setProgress("Paused", 0.5);
  await mock.client.window.setContentProtection(true);
  await mock.client.window.setTitleBarTheme("Dark");
  assert.deepEqual(mock.invocations, [
    { command: "desktop.window.set-icon", args: { root: "assets", relativePath: "app.ico", operation: "read" } },
    { command: "desktop.window.set-represented-file", args: { operation: "read" } },
    { command: "desktop.window.set-progress", args: { state: "Paused", value: 0.5 } },
    { command: "desktop.window.set-content-protection", args: { value: true } },
    { command: "desktop.window.set-titlebar-theme", args: { theme: "Dark" } },
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

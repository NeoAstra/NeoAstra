import assert from "node:assert/strict";
import test from "node:test";
import { updateCommands } from "../dist/index.js";
import { createMockUpdates } from "../dist/testing.js";

test("update renderer commands accept no feed, key, path, helper, RID, or version", async () => {
  const mock = createMockUpdates();
  const status = { mode: "experimental", phase: "idle", currentVersion: "1.0.0", canInstall: false };
  mock.setResult(updateCommands.status, status);
  mock.setResult(updateCommands.check, status);
  mock.setResult(updateCommands.download, status);
  mock.setResult(updateCommands.install, status);
  assert.deepEqual(await mock.client.status(), status);
  await mock.client.check(); await mock.client.download(); await mock.client.installAndRestart();
  assert.deepEqual(mock.invocations, [
    { command: updateCommands.status, args: {} },
    { command: updateCommands.check, args: {} },
    { command: updateCommands.download, args: {} },
    { command: updateCommands.install, args: {} },
  ]);
});

test("update mocks deny by default and contain progress listener failures", async () => {
  const mock = createMockUpdates();
  await assert.rejects(() => mock.client.download(), error => error.code === "permission_denied");
  let received;
  await mock.client.onChanged(() => { throw new Error("contained"); });
  const remove = await mock.client.onChanged(value => { received = value; });
  mock.emit(updateCommands.changed, { mode: "experimental", phase: "downloading", currentVersion: "1", canInstall: false, progressPercent: 25 });
  assert.equal(received.progressPercent, 25); await remove();
});

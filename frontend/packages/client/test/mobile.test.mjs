import assert from "node:assert/strict";
import test from "node:test";
import { keyboardHeight, onKeyboard } from "../dist/index.js";

test("keyboard listeners receive the height and the duration of the keyboard until they stop", () => {
  const target = new EventTarget();
  const received = [];
  const stop = onKeyboard(value => received.push(value), target);
  target.dispatchEvent(new CustomEvent("neoastrakeyboard", { detail: { height: 291, duration: 0.25 } }));
  // A malformed detail is not passed on.
  target.dispatchEvent(new CustomEvent("neoastrakeyboard", { detail: { height: "tall" } }));
  target.dispatchEvent(new CustomEvent("neoastrakeyboard", { detail: { height: 0, duration: 0 } }));
  stop();
  target.dispatchEvent(new CustomEvent("neoastrakeyboard", { detail: { height: 120, duration: 0.25 } }));
  assert.deepEqual(received, [{ height: 291, duration: 0.25 }, { height: 0, duration: 0 }]);
});

test("the keyboard height reads the CSS variable of the root element, 0 without one", () => {
  const root = (value) => ({ style: { getPropertyValue: name => name === "--neoastra-keyboard-height" ? value : "" } });
  assert.equal(keyboardHeight(root("291px")), 291);
  assert.equal(keyboardHeight(root("")), 0);
  assert.equal(keyboardHeight(root("nonsense")), 0);
  assert.equal(keyboardHeight(undefined), 0);
});

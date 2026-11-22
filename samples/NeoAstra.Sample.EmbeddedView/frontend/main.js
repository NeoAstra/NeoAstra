import { connect, onDiagnostic } from "./neoastra-client/index.js";

const placeholder = document.querySelector("#placeholder");
const status = document.querySelector("#status");
onDiagnostic(value => { if (value.level === "error") status.textContent = `${value.code}: ${value.message}`; });

const connection = await connect();
const send = (kind, values = {}) => connection.send({ neoastra: 1, kind, ...values });

// The host places the view over this rectangle, in CSS pixels: it converts zoom and display scale itself.
const reportPlaceholder = () => {
  const { x, y, width, height } = placeholder.getBoundingClientRect();
  if (width > 0 && height > 0) send("place", { x, y, width, height });
};

connection.setReceiveHandler(value => {
  if (value.kind === "status") status.textContent = value.text;
  else if (value.kind === "report") reportPlaceholder();
});

new ResizeObserver(reportPlaceholder).observe(placeholder);
addEventListener("resize", reportPlaceholder);
visualViewport?.addEventListener("resize", reportPlaceholder);
for (const kind of ["toggle", "focus", "move"]) document.querySelector(`#${kind}`).addEventListener("click", () => send(kind));
reportPlaceholder();

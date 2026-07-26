import { isAvailable } from "@neoastra/client";
import { documents } from "../generated/neoastra";

document.body.textContent = isAvailable() ? "NeoAstra available" : "Ordinary browser";
if (isAvailable()) {
  const controller = new AbortController();
  void documents.open({ id: "readme" }, { signal: controller.signal });
  void documents.onChanged(value => { document.title = value.title; });
}

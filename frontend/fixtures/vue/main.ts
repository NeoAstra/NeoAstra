import { createApp } from "vue";
import { isAvailable } from "@neoastra/client";
import { documents } from "../generated/neoastra";

createApp({ template: `<p>${isAvailable() ? "NeoAstra available" : "Ordinary browser"}</p>` }).mount("#app");
if (isAvailable()) void documents.open({ id: "vue" });

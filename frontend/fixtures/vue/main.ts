import { createApp } from "vue";
import { isAvailable } from "@neoastra/client";

createApp({ template: `<p>${isAvailable() ? "NeoAstra available" : "Ordinary browser"}</p>` }).mount("#app");

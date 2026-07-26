import { isAvailable } from "@neoastra/client";

document.body.textContent = isAvailable() ? "NeoAstra available" : "Ordinary browser";

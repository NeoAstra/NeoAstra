import React from "react";
import { createRoot } from "react-dom/client";
import { isAvailable } from "@neoastra/client";

createRoot(document.getElementById("app")!).render(<p>{isAvailable() ? "NeoAstra available" : "Ordinary browser"}</p>);

import React from "react";
import { createRoot } from "react-dom/client";
import { isAvailable } from "@neoastra/client";
import { documents } from "../generated/neoastra";

createRoot(document.getElementById("app")!).render(<p>{isAvailable() ? "NeoAstra available" : "Ordinary browser"}</p>);
if (isAvailable()) void documents.open({ id: "react" });

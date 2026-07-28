import React from "react";
import { createRoot } from "react-dom/client";
import { App } from "./App";
import { installNavigationGuard } from "./security";
import "./style.css";

installNavigationGuard();

const root = document.querySelector("#app");
if (root === null) throw new Error("The #app root element is missing.");

createRoot(root).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>,
);

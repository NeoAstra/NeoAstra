import path from "node:path";
import { rm } from "node:fs/promises";
import { build } from "vite";
import react from "@vitejs/plugin-react";
import vue from "@vitejs/plugin-vue";

const root = path.resolve(import.meta.dirname, "..");
const fixtures = [
  ["vanilla", []],
  ["react", [react()]],
  ["vue", [vue()]],
];
for (const [name, plugins] of fixtures) {
  const fixtureRoot = path.join(root, "fixtures", name);
  await build({ root: fixtureRoot, plugins, logLevel: "error", build: { outDir: "dist", emptyOutDir: true } });
  await rm(path.join(fixtureRoot, "dist"), { recursive: true, force: true });
}

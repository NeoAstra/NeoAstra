import { readFile, readdir } from "node:fs/promises";
import path from "node:path";
import { spawnSync } from "node:child_process";

const root = path.resolve(import.meta.dirname, "..");
const packageRoot = path.join(root, "packages", "client");
const manifest = JSON.parse(await readFile(path.join(packageRoot, "package.json"), "utf8"));
if (manifest.dependencies && Object.keys(manifest.dependencies).length !== 0) throw new Error("The client package must have no runtime dependencies.");
if (manifest.publishConfig?.access !== "public" || manifest.publishConfig?.provenance !== true) throw new Error("Public package provenance publishing is not enabled.");
for (const required of ["LICENSE", "README.md", "provenance.json"]) await readFile(path.join(packageRoot, required));
const provenance = JSON.parse(await readFile(path.join(packageRoot, "provenance.json"), "utf8"));
if (provenance.license !== manifest.license || provenance.runtimeDependencies.length !== 0) throw new Error("Package provenance does not match the manifest.");
const distFiles = await readdir(path.join(packageRoot, "dist"));
if (!["index.js", "index.d.ts", "testing.js", "testing.d.ts"].every(name => distFiles.includes(name))) throw new Error("Package build output is incomplete.");
const production = await readFile(path.join(packageRoot, "dist", "index.js"), "utf8");
if (/\bchrome\s*\.?\s*webview|messageHandlers|\beval\s*\(|\bnew\s+Function\b/.test(production)) throw new Error("Production package contains a backend global or dynamic code execution.");
const packed = spawnSync("npm", ["pack", "--dry-run", "--json"], { cwd: packageRoot, encoding: "utf8", shell: process.platform === "win32" });
if (packed.status !== 0) throw new Error(packed.error?.message || packed.stderr || "npm pack --dry-run failed");
const files = JSON.parse(packed.stdout)[0].files.map(value => value.path);
for (const required of ["LICENSE", "README.md", "provenance.json", "dist/index.js", "dist/index.d.ts", "dist/testing.js", "dist/testing.d.ts"]) {
  if (!files.includes(required)) throw new Error(`Packed client is missing ${required}.`);
}

const repository = path.resolve(root, "..");
const frontendRoots = ["samples", "src/NeoAstra.Conformance/frontend", "src/NeoAstra.Benchmarks/frontend"];
for (const relativeRoot of frontendRoots) {
  const directory = path.join(repository, relativeRoot);
  for (const name of await readdir(directory, { recursive: true })) {
    if (/(^|[\\/])(bin|obj|node_modules)([\\/]|$)/.test(name)) continue;
    if (!/\.(?:html|js|mjs|ts|tsx)$/.test(name)) continue;
    const source = await readFile(path.join(directory, name), "utf8");
    if (/\bchrome\s*\.?\s*webview|messageHandlers|neoastramessage/.test(source)) throw new Error(`Raw backend bridge global in ${path.join(relativeRoot, name)}.`);
  }
}
console.log("Package contents, license, provenance, CSP, and frontend bridge-global checks passed.");

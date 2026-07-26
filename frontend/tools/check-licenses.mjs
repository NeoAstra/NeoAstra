import { readFile } from "node:fs/promises";
import path from "node:path";

const root = path.resolve(import.meta.dirname, "..");
const lock = JSON.parse(await readFile(path.join(root, "package-lock.json"), "utf8"));
const accepted = new Set([
  "0BSD",
  "Apache-2.0",
  "BSD-2-Clause",
  "BSD-3-Clause",
  "ISC",
  "MIT",
  "MPL-2.0",
]);

let reviewed = 0;
for (const [location, manifest] of Object.entries(lock.packages ?? {})) {
  if (!location.startsWith("node_modules/") || manifest.link === true) continue;
  const name = location.slice(location.lastIndexOf("node_modules/") + "node_modules/".length);
  if (typeof manifest.license !== "string" || !accepted.has(manifest.license)) {
    throw new Error(`Unreviewed development dependency license for ${name}@${manifest.version ?? "unknown"}: ${manifest.license ?? "missing"}`);
  }
  reviewed++;
}

console.log(`Reviewed ${reviewed} locked frontend development dependency licenses; the published client has no runtime dependencies.`);

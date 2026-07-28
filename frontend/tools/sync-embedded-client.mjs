import { copyFile, mkdir } from "node:fs/promises";
import path from "node:path";

const repository = path.resolve(import.meta.dirname, "../..");
const source = path.join(repository, "frontend", "packages", "client", "dist");
for (const targetRoot of [
  "samples/NeoAstra.Sample/assets/neoastra-client",
  "samples/NeoAstra.Core.Sample/assets/neoastra-client",
  "src/NeoAstra.Conformance/assets/neoastra-client",
  "src/NeoAstra.Benchmarks/assets/neoastra-client",
]) {
  const target = path.join(repository, targetRoot);
  await mkdir(target, { recursive: true });
  for (const name of ["index.js", "shared.js", "rpc.js", "desktop.js", "updates.js"]) await copyFile(path.join(source, name), path.join(target, name));
}

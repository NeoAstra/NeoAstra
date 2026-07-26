import path from "node:path";
import { readFile, writeFile, mkdir } from "node:fs/promises";

const root = path.resolve(import.meta.dirname, "../..");
const check = process.argv.includes("--check");
const variants = [["neoastra-vanilla", "ts"], ["neoastra-react", "tsx"], ["neoastra-vue", "ts"]];
const fragments = ["src/style.css", "src/security.ts", "src/app.test.ts"];
const sharedConfigPath = path.join(root, "templates/shared/neoastra.json");
const sharedConfig = await readFile(sharedConfigPath, "utf8");
assertNpmPolicy(sharedConfig, sharedConfigPath);
let drift = false;
for (const [name, extension] of variants) {
  const output = path.join(root, "templates", name);
  const expected = new Map();
  expected.set("ClientApp/index.html", (await readFile(path.join(root, "templates/shared/index.html"), "utf8")).replace("__EXT__", extension));
  for (const fragment of fragments) expected.set(`ClientApp/${fragment}`, await readFile(path.join(root, "templates/shared", fragment), "utf8"));
  for (const fragment of ["NeoAstraApp.csproj", "Program.cs", "capabilities/main.json"])
    expected.set(fragment, await readFile(path.join(root, "templates/shared", fragment), "utf8"));
  expected.set("neoastra.json", sharedConfig);
  for (const [relative, content] of expected) {
    const target = path.join(output, relative);
    if (check) {
      let actual;
      try { actual = await readFile(target, "utf8"); }
      catch { console.error(`Missing generated template fragment: ${target}`); drift = true; continue; }
      if (actual !== content) { console.error(`Template fragment drift: ${target}`); drift = true; }
      if (relative === "neoastra.json") {
        try { assertNpmPolicy(actual, target); }
        catch (error) { console.error(error.message); drift = true; }
      }
    } else { await mkdir(path.dirname(target), { recursive: true }); await writeFile(target, content); }
  }
}
if (drift) process.exitCode = 1;

function assertNpmPolicy(content, source) {
  const frontend = JSON.parse(content).frontend;
  if (frontend?.packageManager !== "npm" || frontend?.lockfile !== "ClientApp/package-lock.json")
    throw new Error(`Template npm/lockfile policy is invalid: ${source}`);
}

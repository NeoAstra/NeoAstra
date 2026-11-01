import path from "node:path";
import { readFile, writeFile, mkdir } from "node:fs/promises";

const root = path.resolve(import.meta.dirname, "../..");
const check = process.argv.includes("--check");
// The mobile template shares the page; its project and its application are its own, one project for iOS and Android.
const variants = [["neoastra-vanilla", "ts", true], ["neoastra-react", "tsx", true], ["neoastra-vue", "ts", true], ["neoastra-mobile", "ts", false]];
const fragments = ["src/style.css", "src/app.test.ts"];
let drift = false;
for (const [name, extension, desktop] of variants) {
  const output = path.join(root, "templates", name);
  const expected = new Map();
  expected.set("frontend/index.html", (await readFile(path.join(root, "templates/shared/index.html"), "utf8")).replace("__EXT__", extension));
  for (const fragment of fragments) expected.set(`frontend/${fragment}`, await readFile(path.join(root, "templates/shared", fragment), "utf8"));
  if (desktop) for (const fragment of ["NeoAstraApp.csproj", "Program.cs"])
    expected.set(fragment, await readFile(path.join(root, "templates/shared", fragment), "utf8"));
  for (const [relative, content] of expected) {
    const target = path.join(output, relative);
    if (check) {
      let actual;
      try { actual = await readFile(target, "utf8"); }
      catch { console.error(`Missing generated template fragment: ${target}`); drift = true; continue; }
      if (actual !== content) { console.error(`Template fragment drift: ${target}`); drift = true; }
    } else { await mkdir(path.dirname(target), { recursive: true }); await writeFile(target, content); }
  }
}
if (drift) process.exitCode = 1;

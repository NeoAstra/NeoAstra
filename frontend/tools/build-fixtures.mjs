import path from "node:path";
import { mkdir, readFile, readdir, rm, rmdir, writeFile } from "node:fs/promises";
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

const repositoryRoot = path.resolve(root, "..");
const clientAlias = path.join(root, "packages/client/dist/index.js");
const sourceAliases = [
  { find: /^@neoastra\/client$/, replacement: clientAlias },
  { find: /^react$/, replacement: path.join(root, "node_modules/react/index.js") },
  { find: /^react-dom\/client$/, replacement: path.join(root, "node_modules/react-dom/client.js") },
  { find: /^react\/jsx-runtime$/, replacement: path.join(root, "node_modules/react/jsx-runtime.js") },
  { find: /^react\/jsx-dev-runtime$/, replacement: path.join(root, "node_modules/react/jsx-dev-runtime.js") },
  { find: /^vue$/, replacement: path.join(root, "node_modules/vue/dist/vue.esm-bundler.js") },
];
for (const [name, plugins] of [["neoastra-vanilla", []], ["neoastra-react", [react()]], ["neoastra-vue", [vue()]]]) {
  const templateRoot = path.join(repositoryRoot, "templates", name, "ClientApp");
  const generatedRoot = path.join(templateRoot, "src/generated");
  const generatedFile = path.join(generatedRoot, "neoastra.ts");
  let generated = false;
  try {
    await mkdir(generatedRoot, { recursive: true });
    await writeFile(generatedFile, 'import { invoke } from "@neoastra/client"; export const greeting = { hello: (request: { name: string }) => invoke<{ name: string }, { message: string }>("greeting.hello", request) };\n', { flag: "wx" });
    generated = true;
    await build({ root: templateRoot, configFile: false, base: "./", plugins, logLevel: "error", resolve: { alias: sourceAliases }, build: { outDir: ".neoastra-check-dist", emptyOutDir: true } });
  } finally {
    await rm(path.join(templateRoot, ".neoastra-check-dist"), { recursive: true, force: true });
    if (generated) await rm(generatedFile, { force: true });
    await rmdir(generatedRoot).catch(() => {});
  }
}

const referenceRoot = path.join(repositoryRoot, "samples/NeoAstra.V2.Reference/ClientApp");
const referenceCheckRoot = path.join(referenceRoot, ".neoastra-check-dist");
try {
  await build({ root: referenceRoot, configFile: false, base: "./", plugins: [react()], logLevel: "error", resolve: { alias: sourceAliases }, assetsInclude: ["**/*.ttf"], worker: { format: "es" }, build: { assetsInlineLimit: 0, outDir: ".neoastra-check-dist", emptyOutDir: true } });
  await assertDirectoriesEqual(path.join(referenceRoot, "dist"), referenceCheckRoot);
} finally {
  await rm(referenceCheckRoot, { recursive: true, force: true });
}

async function assertDirectoriesEqual(expectedRoot, actualRoot) {
  const expected = await readDirectory(expectedRoot);
  const actual = await readDirectory(actualRoot);
  const expectedPaths = [...expected.keys()];
  const actualPaths = [...actual.keys()];
  if (expectedPaths.length !== actualPaths.length || expectedPaths.some((value, index) => value !== actualPaths[index])) {
    throw new Error(`Reference prebuilt asset graph is stale: expected [${expectedPaths.join(", ")}], built [${actualPaths.join(", ")}].`);
  }
  for (const file of expectedPaths) {
    if (!expected.get(file).equals(actual.get(file))) throw new Error(`Reference prebuilt asset is stale: ${file}.`);
  }
}

async function readDirectory(rootDirectory, relative = "", files = new Map()) {
  const entries = await readdir(path.join(rootDirectory, relative), { withFileTypes: true });
  entries.sort((left, right) => left.name < right.name ? -1 : left.name > right.name ? 1 : 0);
  for (const entry of entries) {
    const child = relative ? `${relative}/${entry.name}` : entry.name;
    if (entry.isDirectory()) await readDirectory(rootDirectory, child, files);
    else if (entry.isFile()) files.set(child, await readFile(path.join(rootDirectory, child)));
    else throw new Error(`Reference output contains a non-regular entry: ${child}.`);
  }
  return files;
}

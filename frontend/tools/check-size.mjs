import { readFile, readdir } from "node:fs/promises";
import path from "node:path";
import { gzipSync } from "node:zlib";

const dist = path.resolve(import.meta.dirname, "../packages/client/dist");
const files = (await readdir(dist)).filter(name => name.endsWith(".js") && name !== "testing.js");
const source = Buffer.concat(await Promise.all(files.map(name => readFile(path.join(dist, name)))));
const compressedBytes = gzipSync(source, { level: 9 }).byteLength;
const budget = 20 * 1024;
if (compressedBytes > budget) throw new Error(`@neoastra/client production ESM is ${compressedBytes} gzip bytes; budget is ${budget}.`);
console.log(`@neoastra/client production ESM: ${compressedBytes}/${budget} gzip bytes`);

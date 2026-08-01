import { fileURLToPath } from "node:url";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";

export default defineConfig({
  resolve: {
    alias: { "#neoastra": fileURLToPath(new URL("../obj/neoastra/neoastra.ts", import.meta.url)) },
    dedupe: ["@neoastra/client"],
  },
  base: "./",
  plugins: [react()],
  server: {
    fs: { allow: [".."] },
    host: "127.0.0.1",
    strictPort: true,
    port: 5173,
  },
  build: {
    assetsInlineLimit: 0,
    sourcemap: false,
  },
  worker: {
    format: "es",
  },
});

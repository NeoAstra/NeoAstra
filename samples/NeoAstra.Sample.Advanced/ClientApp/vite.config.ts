import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";

export default defineConfig({
  base: "./",
  plugins: [react()],
  server: {
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

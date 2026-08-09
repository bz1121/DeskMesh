import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";
import { fileURLToPath, URL } from "node:url";

const agentOrigin =
  process.env.LANSWITCH_AGENT_ORIGIN ?? "http://127.0.0.1:5616";

export default defineConfig({
  root: fileURLToPath(new URL(".", import.meta.url)),
  base: "/",
  plugins: [react()],
  server: {
    host: "127.0.0.1",
    port: 5173,
    strictPort: false,
    proxy: {
      "/api": {
        target: agentOrigin,
        changeOrigin: false,
        ws: true,
      },
    },
  },
  preview: {
    host: "127.0.0.1",
    port: 4173,
  },
  build: {
    outDir: fileURLToPath(
      new URL("../src/LanSwitch.Agent/wwwroot", import.meta.url),
    ),
    emptyOutDir: false,
    cssCodeSplit: false,
    rollupOptions: {
      input: fileURLToPath(new URL("index.html", import.meta.url)),
      output: {
        entryFileNames: "assets/lanswitch-console.js",
        chunkFileNames: "assets/[name].js",
        assetFileNames: (assetInfo) =>
          assetInfo.name?.endsWith(".css")
            ? "assets/lanswitch-console.css"
            : "assets/[name][extname]",
      },
    },
  },
});

import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  optimizeDeps: {
    // This tells Vite not to pre-bundle the specified package.
    // It will be loaded globally via the <script> tag in index.html instead.
    exclude: ["@webcrypto-local/client"],
  },
});

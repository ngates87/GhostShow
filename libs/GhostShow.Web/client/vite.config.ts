import { defineConfig } from 'vite';
import { svelte } from '@sveltejs/vite-plugin-svelte';

// Built into ../wwwroot, which GhostShow.Web embeds and serves. `npm run dev` serves it here with hot reload,
// passing /api (pairing and the live WebSocket) to a GhostShow running on this computer with the remote switched on.
export default defineConfig({
  plugins: [svelte()],
  build: {
    outDir: '../wwwroot',
    emptyOutDir: true,
    target: 'es2022',
  },
  server: {
    proxy: {
      '/api': { target: 'http://localhost:7700', ws: true },
    },
  },
});

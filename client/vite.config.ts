import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

// Builds straight into the server's wwwroot so `dotnet publish` ships one binary
// with the UI baked in. In dev, /api and /img proxy to the running server.
export default defineConfig({
  plugins: [react(), tailwindcss()],
  build: {
    outDir: '../server/wwwroot',
    emptyOutDir: true,
  },
  server: {
    port: 5173,
    proxy: {
      '/api': 'http://localhost:5188',
      '/img': 'http://localhost:5188',
    },
  },
})

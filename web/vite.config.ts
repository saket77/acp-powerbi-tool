import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    // Everything under /api goes to the ASP.NET Core backend. Proxying rather than calling it
    // cross-origin keeps the browser on one origin, which is what lets the session live in an
    // HttpOnly SameSite=Strict cookie — the arrangement Phase 2 depends on.
    proxy: {
      '/api': {
        target: 'http://localhost:5280',
        changeOrigin: false,
      },
    },
  },
  build: {
    outDir: 'dist',
    sourcemap: true,
  },
})

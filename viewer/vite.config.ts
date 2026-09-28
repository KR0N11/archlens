import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// base './' so the built site works from any folder (GitHub Pages, a static host, or opening dist/ locally via a server).
export default defineConfig({
  plugins: [react()],
  base: './',
  // In dev, /api goes to the ArchLens API (src/ArchLens.Api), so the page and the API share one origin.
  server: {
    proxy: { '/api': 'http://localhost:5080' },
  },
  // elkjs ships as one ~1.4 MB file; splitting it would not make the first diagram appear sooner.
  build: { chunkSizeWarningLimit: 2500 },
})

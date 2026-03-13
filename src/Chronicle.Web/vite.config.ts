import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import path from 'path'
import { readFileSync, existsSync } from 'fs'

// Load ports from the single source of truth at the project root.
// Walk up from this file's directory until ports.json is found.
function loadPorts(): { api: number; web: number } {
  const defaults = { api: 8080, web: 3000 }
  let dir = path.resolve(__dirname)
  for (let i = 0; i < 6; i++) {
    const candidate = path.join(dir, 'ports.json')
    if (existsSync(candidate)) {
      try {
        return { ...defaults, ...JSON.parse(readFileSync(candidate, 'utf-8')) }
      } catch {
        break
      }
    }
    const parent = path.dirname(dir)
    if (parent === dir) break
    dir = parent
  }
  return defaults
}

const ports = loadPorts()
const webPort = process.env.PORT ? parseInt(process.env.PORT, 10) : ports.web

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
    },
  },
  server: {
    port: webPort,
    strictPort: false,
    proxy: {
      // Scan endpoints are long-running: enumerating a large movie folder
      // (500+ subdirectories) can easily exceed 2 minutes.  This rule must
      // come BEFORE the catch-all /api rule so Vite matches it first.
      '/api/v1/scan': {
        target: `http://localhost:${ports.api}`,
        changeOrigin: true,
        timeout: 600_000,      // 10 minutes
        proxyTimeout: 600_000,
      },
      '/api': {
        target: `http://localhost:${ports.api}`,
        changeOrigin: true,
        // Long-running requests (bulk import, metadata refresh) can take >30 s
        // on large libraries.
        timeout: 120_000,
        proxyTimeout: 120_000,
      },
    },
  },
})

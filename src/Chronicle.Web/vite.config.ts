import { defineConfig, createLogger } from 'vite'
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

// Suppress ECONNREFUSED proxy noise when the API is stopped or starting up.
// On dual-stack localhost, Node throws AggregateError whose .code lives on
// errors[0] — not on the top-level object — so we recurse to find it.
const isConnRefused = (e: unknown): boolean => {
  if (!e || typeof e !== 'object') return false
  if ((e as NodeJS.ErrnoException).code === 'ECONNREFUSED') return true
  const agg = e as AggregateError
  return Array.isArray(agg.errors) && agg.errors.some(isConnRefused)
}
const logger = createLogger()
const originalError = logger.error.bind(logger)
logger.error = (msg, options) => {
  if (isConnRefused(options?.error)) return
  originalError(msg, options)
}

export default defineConfig({
  customLogger: logger,
  plugins: [react()],
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
    },
  },
  server: {
    port: webPort,
    host: true,
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

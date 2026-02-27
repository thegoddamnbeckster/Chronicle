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

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
    },
  },
  server: {
    port: ports.web,
    strictPort: false,
    proxy: {
      '/api': {
        target: `http://localhost:${ports.api}`,
        changeOrigin: true,
      },
    },
  },
})

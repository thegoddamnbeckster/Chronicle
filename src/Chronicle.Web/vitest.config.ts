import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'
import path from 'path'

// Separate from vite.config.ts on purpose -- that file also configures the dev server's proxy,
// port discovery, and build chunking, none of which apply to a test run and shouldn't be able to
// break it by accident. Mirrors only what tests actually need: the '@' alias and the React plugin.
export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    css: true,
  },
})

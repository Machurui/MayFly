import { defineConfig } from 'vitest/config'
import vue from '@vitejs/plugin-vue'

// https://vite.dev/config/
export default defineConfig({
  plugins: [vue()],
  server: { proxy: { '/api': 'http://localhost:5000' } },
  test: { environment: 'jsdom', globals: true, passWithNoTests: true },
})

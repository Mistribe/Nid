/// <reference types="vitest/config" />
import { fileURLToPath, URL } from 'node:url';
import tailwindcss from '@tailwindcss/vite';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';
import { securityHeaders } from './src/security-headers.ts';

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: { alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) } },
  server: {
    // Caddy reaches the dev server through host.docker.internal and forwards the public host name.
    host: true,
    port: Number(process.env.PORT ?? 5173),
    strictPort: true,
    allowedHosts: ['nid.localtest.me'],
    headers: securityHeaders({ csp: false }),
  },
  preview: { headers: securityHeaders(), port: 4173, strictPort: true },
  build: {
    manifest: true,
    // every module the entry loads statically goes in one chunk: dozens of small shared chunks cost kilobytes of gzip overhead
    rolldownOptions: { output: { codeSplitting: { groups: [{ name: 'initial', tags: ['$initial'] }] } } },
    chunkSizeWarningLimit: 700,
  },
  test: {
    environment: 'jsdom',
    // One jsdom per worker rather than per file (it was a quarter of the run): files share a worker's window and modules,
    // so src/test/setup.ts resets what a test leaves behind.
    pool: 'threads',
    isolate: false,
    // A pull request's runner has 2 vCPUs, and the default (one less than the CPUs) would run every file in a single worker.
    ...(process.env.CI ? { maxWorkers: 2 } : {}),
    setupFiles: ['./src/test/setup.ts'],
    // A form test typing through jsdom takes 1–3 s on an idle machine, a cold import of a lazy part (calendar, cmdk) more:
    // the 5 s and 10 s defaults timed out under load (two suites at once, CI runners).
    testTimeout: 20_000,
    hookTimeout: 20_000,
    include: ['src/**/*.test.{ts,tsx}', 'eslint-rules/**/*.test.js', 'scripts/**/*.test.js'],
  },
});

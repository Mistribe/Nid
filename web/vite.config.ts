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
  build: { manifest: true },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    include: ['src/**/*.test.{ts,tsx}', 'eslint-rules/**/*.test.js', 'scripts/**/*.test.js'],
  },
});

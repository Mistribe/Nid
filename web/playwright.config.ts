import { defineConfig, devices } from '@playwright/test';

/** Journeys against the Aspire dev stack (web design §7): local only, not in blocking CI. */
export default defineConfig({
  testDir: './e2e',
  timeout: 60_000,
  retries: 0,
  use: {
    baseURL: process.env.NID_URL ?? 'https://nid.localtest.me',
    ignoreHTTPSErrors: true, // Caddy's local certificate authority
    trace: 'retain-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
});

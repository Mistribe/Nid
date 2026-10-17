import { defineConfig, devices } from '@playwright/test';

const QUIET = ['**/export.spec.ts', '**/security.spec.ts'];

/** Journeys against the Aspire dev stack, or the reference compose in deploy/smoke.sh, which CI runs (1F1). */
export default defineConfig({
  testDir: './e2e',
  // Opens the users' sessions at the identity provider once (e2e/global-setup.ts).
  globalSetup: './e2e/global-setup.ts',
  // *.smoke.ts run only from deploy/smoke.sh (NID_SMOKE=1): they restart services or wait for the catalog sync.
  testMatch: process.env.NID_SMOKE ? ['**/*.spec.ts', '**/*.smoke.ts'] : ['**/*.spec.ts'],
  timeout: 60_000,
  retries: 0,
  use: {
    baseURL: process.env.NID_URL ?? 'https://nid.localtest.me',
    ignoreHTTPSErrors: true, // Caddy's local certificate authority
    trace: 'retain-on-failure',
  },
  projects: [
    { name: 'chromium', use: { ...devices['Desktop Chrome'] }, testIgnore: QUIET },
    // Once the other journeys are done: the export reads alice's data from end to end, and « Déconnecter partout » (security.spec)
    // ends every session of bob, which the other journeys sign in.
    { name: 'quiet', use: { ...devices['Desktop Chrome'] }, testMatch: QUIET, dependencies: ['chromium'] },
  ],
});

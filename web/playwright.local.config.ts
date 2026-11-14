import { defineConfig, devices } from '@playwright/test';

/**
 * Issue 215 (local-accounts.md §18.2): the journeys of local accounts, against deploy/standalone without Pocket ID
 * (deploy/standalone/smoke-local.sh, which CI runs). The stack starts empty: each file builds on the one before (the setup
 * creates Alice, who adds Bob…), so the files run one after the other, in this order.
 */
const chrome = devices['Desktop Chrome'];
const steps = ['local-setup', 'local-accounts', 'local-reset', 'local-totp', 'local-admin'];

export default defineConfig({
  testDir: './e2e/local',
  timeout: 60_000,
  retries: 0,
  workers: 1,
  use: {
    baseURL: process.env.NID_URL ?? 'http://localhost:8080',
    trace: 'retain-on-failure',
  },
  projects: steps.map((step, i) => ({
    name: step,
    use: { ...chrome },
    testMatch: `${step}.spec.ts`,
    dependencies: i === 0 ? [] : [steps[i - 1]],
  })),
});

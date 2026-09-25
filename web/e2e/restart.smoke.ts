import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { expect, test } from '@playwright/test';
import { signIn } from './session';

// 1F1 Review Focus 2: Data Protection keys on a volume keep a session through a restart of nid-api (deploy/smoke.sh).
// Outside test-results/: Playwright empties that folder at the start of every run, and this file spans two runs.
const STATE = join(tmpdir(), 'nid-smoke-restart-state.json');

test('before: alice signs in and keeps her session', async ({ page }) => {
  await signIn(page, 'alice', '/subscriptions');
  await page.context().storageState({ path: STATE });
});

test.describe('after', () => {
  test.use({ storageState: STATE });
  test('after: the session survives a restart of nid-api', async ({ page }) => {
    const session = (await (await page.request.get('/auth/session')).json()) as { authenticated: boolean };
    expect(session.authenticated).toBe(true);
  });
});

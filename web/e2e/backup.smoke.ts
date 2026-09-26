import { readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { expect, test } from '@playwright/test';
import { signIn } from './session';

// 1F2 design §8: a session and the subscriptions from before the backup are back after a restore into a new stack.
// Outside test-results/: Playwright empties that folder at the start of every run, and these files span two runs.
const STATE = join(tmpdir(), 'nid-smoke-backup-state.json');
const IDS = join(tmpdir(), 'nid-smoke-backup-ids.json');

async function subscriptionIds(page: import('@playwright/test').Page): Promise<string[]> {
  const response = await page.request.get('/api/v1/subscriptions?scope=me');
  expect(response.ok()).toBe(true);
  const body = (await response.json()) as { items: Array<{ id: string }> };
  return body.items.map((s) => s.id).sort();
}

test('before: alice signs in and notes her subscriptions', async ({ page }) => {
  await signIn(page, 'alice', '/subscriptions');
  const ids = await subscriptionIds(page);
  expect(ids.length).toBeGreaterThan(0);
  writeFileSync(IDS, JSON.stringify(ids));
  await page.context().storageState({ path: STATE });
});

test.describe('after', () => {
  test.use({ storageState: STATE });
  test('after: the session and the subscriptions are back after the restore', async ({ page }) => {
    const session = (await (await page.request.get('/auth/session')).json()) as { authenticated: boolean };
    expect(session.authenticated).toBe(true);
    expect(await subscriptionIds(page)).toEqual(JSON.parse(readFileSync(IDS, 'utf8')) as string[]);
  });
});

import { expect, test } from '@playwright/test';
import { signIn } from './session';

// 4C2: a token proposes a value; alice sees the badge, approves it, and the envelope shows it.
test('alice approves a value an assistant proposed', async ({ page, playwright }) => {
  const name = `PEA ${Date.now()}`;
  await signIn(page, 'alice', '/investments/new');
  await page.getByLabel('Nom', { exact: true }).fill(name);
  await page.getByLabel('Type').selectOption('pea');
  await page.getByRole('button', { name: 'Créer l’enveloppe' }).click();
  await expect(page.getByRole('heading', { name, level: 1 })).toBeVisible();
  const envelope = page.url().split('/').pop()!;

  // A « Lecture + propositions » token, created through the API with the session's CSRF token.
  const session = (await (await page.request.get('/auth/session')).json()) as { csrf: string };
  const created = await page.request.post('/api/v1/core/me/tokens', {
    headers: { 'X-Nid-Csrf': session.csrf }, data: { name: `E2E ${Date.now()}`, preset: 'propose', expiresInDays: 30 },
  });
  expect(created.ok()).toBe(true);
  const secret = ((await created.json()) as { secret: string }).secret;

  const outside = await playwright.request.newContext({ baseURL: 'https://nid.localtest.me', ignoreHTTPSErrors: true });
  const today = new Date().toISOString().slice(0, 10);
  const submitted = await outside.post('/api/v1/core/proposals', {
    headers: { Authorization: `Bearer ${secret}` },
    data: { kind: 'investments.record_valuation', targetItemId: envelope, payload: { asOf: today, amount: { amount: '12345.67', currency: 'EUR' } }, sourceLabel: 'Relevé E2E' },
  });
  expect(submitted.status()).toBe(201);
  await outside.dispose();

  await page.goto('/proposals');
  await expect(page.getByRole('navigation', { name: 'Navigation principale' }).getByRole('link', { name: /À valider/ })).toContainText(/\d/);
  await page.getByRole('region', { name: 'Relevé E2E' }).getByRole('link', { name: new RegExp(name) }).click();
  await expect(page.getByRole('row', { name: /Valeur proposée/ })).toContainText('12 345,67');
  await page.getByRole('button', { name: 'Approuver' }).click();
  await expect(page.getByRole('status').filter({ hasText: 'Proposition approuvée.' })).toBeVisible();

  await page.goto(`/investments/${envelope}`);
  await expect(page.getByText('12 345,67', { exact: false }).first()).toBeVisible();
});

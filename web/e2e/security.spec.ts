import { expect, test } from '@playwright/test';
import { revokeTokens, signIn } from './session';

// 1F3a spec §9 test 9: sign in, see it, sign out everywhere, land signed out.
test('an adult sees their sign-in and signs out everywhere', async ({ page }) => {
  await signIn(page, 'bob', '/security');

  await expect(page.getByRole('row', { name: /Connexion/ }).first()).toBeVisible();

  await page.getByRole('button', { name: 'Déconnecter partout' }).click();
  await page.getByRole('button', { name: 'Confirmer' }).click();

  await expect(page.getByRole('heading', { name: 'Vous êtes déconnecté de Nid' })).toBeVisible();
});

// 4A: create a token, use it from outside the browser, revoke it, and it stops working at once.
test('an adult creates a token, calls the API with it, and revokes it', async ({ page, playwright }) => {
  const name = `Jeton ${Date.now()}`;
  await signIn(page, 'alice', '/security');

  await page.getByRole('button', { name: 'Créer un jeton' }).click();
  const dialog = page.getByRole('dialog', { name: 'Créer un jeton' });
  await dialog.getByLabel('Nom').fill(name);
  await dialog.getByRole('radio', { name: /Lecture seule/ }).check();
  await dialog.getByRole('button', { name: 'Créer' }).click();
  const created = page.getByRole('dialog', { name: 'Votre nouveau jeton' });
  const secret = (await created.locator('code').textContent())!.trim();
  expect(secret).toMatch(/^nid_pat_[a-z0-9]{8}_[A-Za-z0-9_-]{43}$/);
  await created.getByRole('button', { name: 'J’ai copié le jeton' }).click();

  // A context with no cookie: only the token speaks.
  const outside = await playwright.request.newContext({ baseURL: 'https://nid.localtest.me', ignoreHTTPSErrors: true });
  const headers = { Authorization: `Bearer ${secret}` };
  expect((await outside.get('/api/v1/platform/capabilities', { headers })).status()).toBe(200);

  const row = page.getByRole('row', { name: new RegExp(name) });
  await expect(row).toContainText('Lecture seule');
  await row.getByRole('button', { name: 'Révoquer' }).click();
  await row.getByRole('button', { name: 'Confirmer' }).click();
  await expect(page.getByRole('status').filter({ hasText: 'Jeton révoqué.' })).toBeVisible();
  await expect(row).toContainText('Révoqué');

  expect((await outside.get('/api/v1/platform/capabilities', { headers })).status()).toBe(401);
  await outside.dispose();
});

// 4B: with a token made in the interface, an MCP client lists the persons.
test('an adult plugs an MCP client in with a token', async ({ page, playwright }) => {
  await signIn(page, 'alice', '/security');
  await revokeTokens(page, 'MCP '); // this test never revokes its token: earlier runs' would fill the 10 allowed
  await page.reload();
  await expect(page.getByRole('region', { name: 'Brancher un assistant' })).toContainText('/mcp');

  await page.getByRole('button', { name: 'Créer un jeton' }).click();
  const dialog = page.getByRole('dialog', { name: 'Créer un jeton' });
  await dialog.getByLabel('Nom').fill(`MCP ${Date.now()}`);
  await dialog.getByRole('radio', { name: /Lecture seule/ }).check();
  await dialog.getByRole('button', { name: 'Créer' }).click();
  const created = page.getByRole('dialog', { name: 'Votre nouveau jeton' });
  const secret = (await created.locator('code').textContent())!.trim();
  await created.getByRole('button', { name: 'J’ai copié le jeton' }).click();

  const outside = await playwright.request.newContext({ baseURL: 'https://nid.localtest.me', ignoreHTTPSErrors: true });
  const headers = {
    Authorization: `Bearer ${secret}`, Accept: 'application/json, text/event-stream',
    'Content-Type': 'application/json', 'MCP-Protocol-Version': '2025-11-25',
  };
  const call = await outside.post('/mcp', {
    headers, data: { jsonrpc: '2.0', id: 1, method: 'tools/call', params: { name: 'list_persons', arguments: {} } },
  });
  expect(call.status()).toBe(200);
  const body = await call.text();
  expect(body).toContain('"result"');
  expect(body).not.toContain('"isError":true');

  // The page read its events before the call: reload to see the assistant's read.
  await page.reload();
  await expect(page.getByRole('row', { name: /Lecture par l’assistant/ }).first()).toBeVisible();
  await outside.dispose();
});

import { expect, test } from '@playwright/test';
import { toasts } from './controls';
import { revokeTokens, signIn } from './session';

// 1F3a spec §9 test 9: sign in, see it, sign out everywhere, land signed out.
test('an adult sees their sign-in and signs out everywhere', async ({ page }) => {
  await signIn(page, 'bob', '/security?tab=activity');

  await expect(page.getByRole('row', { name: /Connexion/ }).first()).toBeVisible();

  await page.getByRole('tab', { name: 'Connexion' }).click();
  await page.getByRole('button', { name: 'Déconnecter partout' }).click();
  await page.getByRole('button', { name: 'Confirmer' }).click();

  await expect(page.getByRole('heading', { name: 'Vous êtes déconnecté de Nid' })).toBeVisible();
});

// 4A: create a token, use it from outside the browser, revoke it, and it stops working at once.
test('an adult creates a token, calls the API with it, and revokes it', async ({ page, playwright }) => {
  const name = `Jeton ${Date.now()}`;
  await signIn(page, 'alice', '/security?tab=tokens');

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
  await row.getByRole('button', { name: `Actions du jeton ${name}` }).click();
  await page.getByRole('menuitem', { name: 'Révoquer' }).click();
  await page.getByRole('alertdialog').getByRole('button', { name: 'Confirmer' }).click();
  await expect(toasts(page)).toContainText('Jeton révoqué.');
  // #308: revoked tokens wait, dimmed, under the active ones.
  await page.getByRole('button', { name: /Afficher les? jetons? révoqués? ou expirés?/ }).click();
  await expect(page.getByRole('row', { name: new RegExp(name) })).toContainText('Révoqué');

  expect((await outside.get('/api/v1/platform/capabilities', { headers })).status()).toBe(401);
  await outside.dispose();
});

// 4B: with a token made in the interface, an MCP client lists the persons.
test('an adult plugs an MCP client in with a token', async ({ page, playwright }) => {
  await signIn(page, 'alice', '/security?tab=tokens');
  await revokeTokens(page, 'MCP '); // this test never revokes its token: earlier runs' would fill the 10 allowed
  await page.reload();
  await page.getByRole('button', { name: 'Brancher un assistant' }).click();
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

  // The assistant's reads have their own filter in the Activité tab.
  await page.goto('/security?tab=activity&show=assistant');
  await expect(page.getByRole('row', { name: /Lecture par l’assistant/ }).first()).toContainText('list_persons');
  await outside.dispose();
});

// #308: at a phone's width nothing scrolls sideways, and a token's ⋯ is within reach.
test('the security page fits a phone', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await signIn(page, 'alice', '/security?tab=tokens');
  await revokeTokens(page, 'Phone ');
  const session = (await (await page.request.get('/auth/session')).json()) as { csrf: string };
  await page.request.post('/api/v1/core/me/tokens', { headers: { 'X-Nid-Csrf': session.csrf }, data: { name: `Phone ${Date.now()}`, preset: 'read', expiresInDays: 30 } });
  await page.reload();

  // Neither the page nor any box inside it (a table's own scroller) is wider than the screen.
  // A string: the e2e project has no DOM types.
  const fits = () => page.evaluate<boolean>(`document.documentElement.scrollWidth <= window.innerWidth
    && [...document.querySelectorAll('[data-pane="content"] *')].every((el) => !['auto', 'scroll'].includes(getComputedStyle(el).overflowX) || el.scrollWidth <= el.clientWidth + 1)`);
  const menu = page.getByRole('button', { name: /Actions du jeton Phone / }).first();
  await expect(menu).toBeInViewport();
  expect(await fits()).toBe(true);
  for (const tab of ['Connexion', 'Activité']) {
    await page.getByRole('tab', { name: tab }).click();
    await expect(page.getByRole('tab', { name: tab, selected: true })).toBeVisible();
    expect(await fits()).toBe(true);
  }
});

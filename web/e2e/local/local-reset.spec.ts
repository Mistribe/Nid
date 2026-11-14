import { expect, test } from '@playwright/test';
import { ALICE, BOB, BOB_RESET, createLink, newPage, passwordField, signInWithPassword } from './accounts';

// local-accounts.md §8, §18.2: Bob forgot his password; Alice gives him a reset link, which closes his open sessions.
test('a reset link replaces the password and closes the sessions already open', async ({ page, browser }) => {
  const bob = await newPage(browser);
  await signInWithPassword(bob, BOB.login, BOB.password);

  await signInWithPassword(page, ALICE.login, ALICE.password, '/admin/users');
  const link = await createLink(page, 'Bob', 'Créer un lien de mot de passe');

  const phone = await newPage(browser);
  await phone.goto(link);
  await expect(phone.getByRole('heading', { name: 'Choisir un nouveau mot de passe' })).toBeVisible();
  await expect(phone.getByLabel('Identifiant')).toHaveValue(BOB.login);
  await passwordField(phone, 'Nouveau mot de passe').fill(BOB_RESET);
  await phone.getByRole('button', { name: 'Enregistrer le mot de passe' }).click();
  await phone.waitForURL((url) => url.pathname === '/login');

  // Bob's session opened before the reset is over at its next request.
  const session = (await (await bob.request.get('/auth/session')).json()) as { authenticated: boolean };
  expect(session.authenticated).toBe(false);

  await signInWithPassword(phone, BOB.login, BOB_RESET);
  await expect(phone.getByRole('heading', { level: 1 })).toBeVisible();
});

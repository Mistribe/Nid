import { expect, test } from '@playwright/test';
import { ALICE, BOB, createLink, newPage, passwordField, signInWithPassword } from './accounts';

// local-accounts.md §7, §18.2: Alice adds Bob without an email and passes him a link; Bob chooses his identifier and password.
test('an adult added without an email chooses an identifier and a password through a one-time link', async ({ page, browser }) => {
  await signInWithPassword(page, ALICE.login, ALICE.password, '/admin/users');
  await page.getByRole('button', { name: 'Ajouter un adulte' }).click();
  const add = page.getByRole('dialog', { name: 'Ajouter un adulte' });
  await add.getByLabel('Nom').fill('Bob');
  await add.getByRole('button', { name: 'Ajouter' }).click();
  await expect(page.getByRole('row', { name: /Bob/ })).toContainText('En attente');

  const link = await createLink(page, 'Bob', 'Créer son lien d\'accès');
  await expect(page.getByRole('row', { name: /Bob/ })).toContainText('Lien en attente');

  const bob = await newPage(browser);
  await bob.goto(link);
  await expect(bob.getByRole('heading', { name: 'Bienvenue dans Nid' })).toBeVisible();
  await bob.getByLabel('Identifiant').fill(BOB.login);
  await passwordField(bob, 'Nouveau mot de passe').fill(BOB.password);
  await bob.getByRole('button', { name: 'Créer mon accès' }).click();
  await bob.waitForURL((url) => url.pathname === '/login');

  // A wrong password: one message, whatever was wrong.
  await bob.getByLabel('Identifiant').fill(BOB.login);
  await passwordField(bob).fill('not the right one at all');
  await bob.getByRole('button', { name: 'Se connecter' }).click();
  await expect(bob.getByRole('alert')).toContainText('Identifiant ou mot de passe incorrect');

  await signInWithPassword(bob, BOB.login, BOB.password);
  await bob.getByRole('button', { name: 'Bob', exact: true }).click();
  await bob.getByRole('menuitem', { name: 'Se déconnecter' }).click();
  await bob.waitForURL((url) => url.pathname === '/signed-out');

  // Used once, the link does not work any more.
  const again = await newPage(browser);
  await again.goto(link);
  await expect(again.getByText(/Ce lien ne fonctionne plus/)).toBeVisible();

  await page.reload();
  await expect(page.getByRole('row', { name: /Bob/ })).toContainText('Actif');
});

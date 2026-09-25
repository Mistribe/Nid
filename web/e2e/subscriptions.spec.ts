import { expect, test } from '@playwright/test';
import { signIn } from './session';

test('alice adds a recurring subscription and sees its cost; bob cannot reach her private one', async ({ browser }) => {
  const stamp = Date.now().toString(36);
  const name = `E2E privé ${stamp}`;

  // Separate contexts: each adult has their own session cookie.
  const alice = await (await browser.newContext({ ignoreHTTPSErrors: true })).newPage();
  await signIn(alice, 'alice', '/subscriptions');
  await alice.getByRole('link', { name: 'Ajouter un abonnement' }).first().click();
  // The wizard loads lazily: wait for it, then match labels exactly ("Nom" is also in "Rechercher par nom").
  await expect(alice.getByRole('heading', { level: 1, name: 'Ajouter un abonnement' })).toBeVisible();
  await alice.getByLabel('Nom', { exact: true }).fill(name);
  await alice.getByLabel('Privé', { exact: true }).check();
  await alice.getByRole('button', { name: 'Continuer' }).click();
  await alice.getByLabel('Début', { exact: true }).fill('2025-01-01');
  await alice.getByLabel('Montant', { exact: true }).fill('7,49');
  await alice.getByRole('button', { name: 'Continuer' }).click();
  await alice.getByRole('button', { name: "Ajouter l'abonnement" }).click();

  await expect(alice.getByRole('heading', { level: 1, name })).toBeVisible();
  const detailUrl = new URL(alice.url()).pathname;
  await alice.goto('/subscriptions?scope=me');
  await expect(alice.getByRole('link', { name: new RegExp(name) })).toContainText('7,49');

  const bob = await (await browser.newContext({ ignoreHTTPSErrors: true })).newPage();
  await signIn(bob, 'bob', '/subscriptions');
  await bob.goto('/subscriptions?scope=me');
  await expect(bob.getByRole('heading', { level: 1, name: 'Abonnements' })).toBeVisible();
  await expect(bob.getByText(name)).toHaveCount(0);
  await bob.goto(detailUrl);
  await expect(bob.getByText('Cet élément est introuvable.')).toBeVisible();
});

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
  await alice.getByRole('button', { name: 'Continuer' }).click(); // the type step: Récurrent is preselected
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

test('alice records a free trial followed by a monthly price', async ({ browser }) => {
  const alice = await browser.newPage();
  await signIn(alice, 'alice', '/subscriptions/new');
  const name = `Essai E2E ${Date.now().toString(36)}`;
  await alice.getByRole('radio', { name: /Essai ou offre de lancement/ }).check();
  await alice.getByRole('button', { name: 'Continuer' }).click();
  await alice.getByLabel('Nom', { exact: true }).fill(name);
  await alice.getByRole('button', { name: 'Continuer' }).click();
  await alice.getByLabel('Début', { exact: true }).fill('2026-01-01');
  await alice.getByRole('button', { name: 'Continuer' }).click();
  await expect(alice.getByRole('radio', { name: 'Un abonnement récurrent' })).toBeChecked();
  await alice.getByLabel('Montant', { exact: true }).fill('9,99');
  await alice.getByRole('button', { name: 'Continuer' }).click();
  await alice.getByRole('button', { name: "Ajouter l'abonnement" }).click();

  await expect(alice.getByRole('heading', { level: 1, name })).toBeVisible();
  await expect(alice.getByText('Essai', { exact: false }).first()).toBeVisible(); // the coverage band's legend
  // The free trial covers one month from 2026-01-01: the monthly price's first charge follows it, on 2026-02-01
  // (final review, finding 3; 1E2b1 design §5). "Prochaine échéance" moves on with today's date, so the covered
  // periods table (a historical record) is what shows this first charge, not that paragraph.
  await expect(alice.getByRole('row', { name: /^1 février 2026.*Payé/ })).toBeVisible();
});

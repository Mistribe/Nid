import { expect, test } from '@playwright/test';
import { fillDate, pick, toasts } from './controls';
import { signIn } from './session';

test('alice records a car bought for 18 000 €, values it, and finds it under « Autres biens » on the dashboard', async ({ browser }) => {
  const name = `Voiture ${Date.now()}`;
  const alice = await browser.newPage();
  await signIn(alice, 'alice', '/other-assets');

  await alice.goto('/other-assets/new');
  await alice.getByLabel('Nom', { exact: true }).fill(name);
  await pick(alice, 'Type', 'Véhicule');
  await alice.getByRole('radio', { name: 'Propriétaire' }).click();
  await alice.getByLabel('Montant').fill('18000');
  await fillDate(alice, 'Date', '2022-03-15');
  await alice.getByRole('radio', { name: 'Oui, partagé' }).click();
  await alice.getByRole('button', { name: 'Ajouter le bien' }).click();
  await expect(alice.getByRole('heading', { name, level: 1 })).toBeVisible();

  // #778: the car's purchase price is old, so its notice offers « Ajouter une valeur » too; both open the same dialog.
  await alice.getByRole('button', { name: 'Ajouter une valeur' }).first().click();
  const dialog = alice.getByRole('dialog', { name: 'Nouvelle valeur' });
  await dialog.getByLabel('Montant').fill('12000');
  await dialog.getByRole('button', { name: 'Enregistrer' }).click();
  await expect(toasts(alice)).toContainText('Valeur ajoutée.');

  await alice.goto('/dashboard');
  const worth = alice.getByRole('button', { name: /Patrimoine net/ });
  await expect(worth).toContainText('€');
  await worth.click();
  const split = alice.getByRole('dialog', { name: 'Patrimoine net' }).getByRole('table', { name: 'Valeur de chaque élément' });
  await expect(split.getByRole('columnheader', { name: 'Autres biens' })).toBeVisible();
  await expect(split.getByRole('row', { name: new RegExp(name) })).toContainText('12');
});

import { expect, test } from '@playwright/test';
import { signIn } from './session';

test('alice follows a private PEA and bob never sees it', async ({ browser }) => {
  const name = `PEA ${Date.now()}`;
  const alice = await browser.newPage();
  await signIn(alice, 'alice', '/investments/new');

  await alice.getByLabel('Nom').fill(name);
  await alice.getByLabel('Type').selectOption('pea');
  await alice.getByRole('radio', { name: /Privé/ }).check();
  await alice.getByRole('checkbox', { name: 'Versements antérieurs' }).check();
  await alice.getByLabel('Date des versements antérieurs').fill('2024-12-31');
  await alice.getByLabel('Montant des versements antérieurs').fill('20000');
  await alice.getByRole('checkbox', { name: 'Première valeur' }).check();
  await alice.getByLabel('Date de la première valeur').fill('2024-12-31');
  await alice.getByLabel('Montant de la première valeur').fill('21000');
  await alice.getByRole('button', { name: 'Créer l’enveloppe' }).click();

  await expect(alice.getByRole('heading', { name, level: 1 })).toBeVisible();
  const url = alice.url();

  const flow = alice.getByRole('form', { name: 'Nouveau flux' });
  await flow.getByLabel('Nature').selectOption('contribution');
  await flow.getByLabel('Date').fill('2025-06-15');
  await flow.getByLabel('Montant').fill('2000');
  await flow.getByRole('button', { name: 'Ajouter le flux' }).click();
  await expect(flow.getByRole('status')).toHaveText('Flux ajouté.');

  const value = alice.getByRole('form', { name: 'Nouvelle valeur' });
  await value.getByLabel('Date').fill('2025-09-30');
  await value.getByLabel('Montant').fill('25000');
  await value.getByRole('button', { name: 'Enregistrer la valeur' }).click();

  const figures = alice.getByRole('region', { name: 'Valeur et gain' });
  await expect(figures).toContainText('3 000,00 €');
  await expect(figures).toContainText('13,6 %');

  const bob = await browser.newPage();
  await signIn(bob, 'bob', '/investments');
  // The household scope lists every envelope alice and bob can each see; waiting for the loaded list or the empty
  // state (rather than the always-rendered heading) is a positive signal that the query has actually settled.
  await expect(bob.getByRole('list', { name: 'Liste des enveloppes' }).or(bob.getByRole('heading', { name: 'Aucune enveloppe pour l’instant' }))).toBeVisible();
  await expect(bob.getByText(name)).toHaveCount(0);
  await bob.goto(url);
  await expect(bob.getByText('Cet élément est introuvable.')).toBeVisible();
});

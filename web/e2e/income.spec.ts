import { expect, test } from '@playwright/test';
import { signIn } from './session';

test('alice keeps a private salary up to date and bob never sees it', async ({ browser }) => {
  const name = `Salaire ${Date.now()}`;
  const alice = await browser.newPage();
  await signIn(alice, 'alice', '/income/new');

  await alice.getByLabel('Nom', { exact: true }).fill(name);
  await alice.getByLabel('Type').selectOption('salary');
  await alice.getByRole('radio', { name: 'Non, privé' }).check();
  const base = alice.getByRole('group', { name: 'Composante 1' });
  await expect(base.getByLabel('Libellé')).toHaveValue('Salaire de base');
  await base.getByLabel('Montant net').fill('2300');
  await base.getByLabel('Début', { exact: true }).fill('2025-01-01');
  await alice.getByRole('button', { name: 'Ajouter une composante' }).click();
  const bonus = alice.getByRole('group', { name: 'Composante 2' });
  await bonus.getByLabel('Libellé').fill('Prime annuelle');
  await bonus.getByLabel('Montant net').fill('1800');
  await bonus.getByLabel('Fréquence').selectOption('yearly');
  await bonus.getByLabel('Début', { exact: true }).fill('2024-12-15');
  await alice.getByRole('button', { name: 'Créer le revenu' }).click();

  await expect(alice.getByRole('heading', { name, level: 1 })).toBeVisible();
  const url = alice.url();

  await alice.goto('/income?scope=me');
  await expect(alice.getByRole('listitem').filter({ hasText: name })).toContainText('2 450,00 € par mois');

  await alice.goto(url);
  const prime = alice.getByRole('table', { name: 'Prime annuelle' });
  await prime.getByRole('button', { name: 'Corriger' }).click();
  const correction = alice.getByRole('form', { name: 'Corriger la période' });
  await correction.getByLabel('Montant net').fill('1900');
  await correction.getByRole('button', { name: 'Enregistrer' }).click();
  const phases = alice.getByRole('region', { name: 'Périodes' });
  await expect(phases.getByRole('status')).toHaveText('Période corrigée.');
  await expect(alice.getByRole('table', { name: 'Prime annuelle' })).toContainText('1 900,00 €');

  await alice.getByRole('button', { name: 'Ajouter une période' }).click();
  const added = alice.getByRole('form', { name: 'Ajouter une période' });
  await added.getByLabel('Libellé').fill('Prime de vacances');
  await added.getByLabel('Montant net').fill('500');
  await added.getByLabel('Fréquence').selectOption('yearly');
  await added.getByLabel('Début', { exact: true }).fill('2026-07-01');
  await added.getByRole('button', { name: 'Ajouter la période' }).click();
  await expect(phases.getByRole('status')).toHaveText('Période ajoutée.');
  await expect(alice.getByRole('table', { name: 'Prime de vacances' })).toBeVisible();

  const bob = await browser.newPage();
  await signIn(bob, 'bob', '/income');
  await expect(bob.getByRole('heading', { name: 'Revenus' })).toBeVisible();
  await expect(bob.getByText(name)).toHaveCount(0);
  await bob.goto(url);
  await expect(bob.getByText('Cet élément est introuvable.')).toBeVisible();
});

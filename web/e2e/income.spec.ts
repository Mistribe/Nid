import { expect, test } from '@playwright/test';
import { fillDate, toasts } from './controls';
import { signIn } from './session';

test('alice keeps a private salary up to date and bob never sees it', async ({ browser }) => {
  const name = `Salaire ${Date.now()}`;
  const alice = await browser.newPage();
  await signIn(alice, 'alice', '/income/new');

  await alice.getByLabel('Nom', { exact: true }).fill(name);
  await alice.getByRole('combobox', { name: 'Type' }).click();
  await alice.getByRole('option', { name: 'Salaire' }).click();
  await alice.getByRole('radio', { name: 'Non, privé' }).click();
  await alice.getByRole('button', { name: 'Continuer' }).click();
  const base = alice.getByRole('group', { name: 'Composante 1' });
  await expect(base.getByLabel('Libellé')).toHaveValue('Salaire de base');
  await base.getByLabel('Montant net').fill('2300');
  await fillDate(base, 'Début', '2025-01-01');
  await alice.getByRole('button', { name: 'Ajouter une composante' }).click();
  const bonus = alice.getByRole('group', { name: 'Composante 2' });
  await bonus.getByLabel('Libellé').fill('Prime annuelle');
  await bonus.getByLabel('Montant net').fill('1800');
  await bonus.getByRole('combobox', { name: 'Fréquence' }).click();
  await alice.getByRole('option', { name: 'Chaque année' }).click();
  await fillDate(bonus, 'Début', '2024-12-15');
  await alice.getByRole('button', { name: 'Créer le revenu' }).click();

  await expect(alice.getByRole('heading', { name, level: 1 })).toBeVisible();
  const url = alice.url();

  await alice.goto('/income?scope=me');
  await expect(alice.getByRole('table', { name: 'Liste des revenus' }).getByRole('row').filter({ hasText: name })).toContainText('2 450,00 €');

  await alice.goto(url);
  const timeline = alice.getByRole('list', { name: 'Changements, du plus récent au plus ancien' });
  await timeline.getByRole('button', { name: /^Actions de la période Prime annuelle/ }).first().click();
  await alice.getByRole('menuitem', { name: 'Corriger' }).click();
  const correction = alice.getByRole('form', { name: 'Corriger la période' });
  await correction.getByLabel('Montant net').fill('1900');
  await correction.getByRole('button', { name: 'Enregistrer' }).click();
  const status = toasts(alice);
  await expect(status).toContainText('Période corrigée.');
  await expect(timeline).toContainText('1 900,00 €');

  await alice.getByRole('button', { name: 'Autres actions' }).click();
  await alice.getByRole('menuitem', { name: 'Ajouter une composante' }).click();
  const added = alice.getByRole('form', { name: 'Ajouter une période' });
  await added.getByLabel('Libellé').fill('Prime de vacances');
  await added.getByLabel('Montant net').fill('500');
  await added.getByRole('combobox', { name: 'Fréquence' }).click();
  await alice.getByRole('option', { name: 'Chaque année' }).click();
  await fillDate(added, 'Début', '2026-07-01');
  await added.getByRole('button', { name: 'Ajouter la période' }).click();
  await expect(status).toContainText('Période ajoutée.');
  await expect(timeline).toContainText('Prime de vacances');

  const bob = await browser.newPage();
  await signIn(bob, 'bob', '/income?scope=me');
  await expect(bob.getByRole('heading', { name: 'Revenus' })).toBeVisible();
  await expect(bob.getByText(name)).toHaveCount(0);
  await bob.goto(url);
  await expect(bob.getByRole('heading', { name: 'Page introuvable', level: 1 })).toBeVisible();
});

test('alice changes an amount, records a one-off, ends a source and reopens it', async ({ browser }) => {
  const name = `Revenus ${Date.now()}`;
  const alice = await browser.newPage();
  await signIn(alice, 'alice', '/income/new');

  await alice.getByLabel('Nom', { exact: true }).fill(name);
  await alice.getByRole('radio', { name: 'Non, privé' }).click();
  await alice.getByRole('button', { name: 'Continuer' }).click();
  const base = alice.getByRole('group', { name: 'Composante 1' });
  await base.getByLabel('Montant net').fill('2300');
  await fillDate(base, 'Début', '2025-01-01');
  await alice.getByRole('button', { name: 'Créer le revenu' }).click();
  await expect(alice.getByRole('heading', { name, level: 1 })).toBeVisible();

  // A raise at the first payment date offered (a past one, so ending today stays possible).
  const timeline = alice.getByRole('list', { name: 'Changements, du plus récent au plus ancien' });
  await timeline.getByRole('button', { name: /^Actions de la période Salaire de base/ }).first().click();
  await alice.getByRole('menuitem', { name: 'Changer le montant' }).click();
  const change = alice.getByRole('form', { name: 'Changer le montant' });
  await change.getByRole('combobox', { name: 'Date d’effet' }).click();
  await alice.getByRole('option').first().click();
  await change.getByLabel('Montant net').fill('2500');
  await change.getByRole('button', { name: 'Enregistrer le changement' }).click();
  await expect(toasts(alice)).toContainText('Montant changé.');
  await expect(timeline).toContainText('2 500,00 €');

  await alice.getByRole('tab', { name: 'Exceptionnels' }).click();
  // The header's « + Exceptionnel » bears the same name: the section's own button.
  await alice.getByRole('region', { name: 'Revenus exceptionnels' }).getByRole('button', { name: 'Ajouter un revenu exceptionnel' }).click();
  const oneOff = alice.getByRole('form', { name: 'Nouveau revenu exceptionnel' });
  await oneOff.getByLabel('Montant').fill('400');
  await oneOff.getByRole('button', { name: 'Ajouter', exact: true }).click();
  await expect(toasts(alice)).toContainText('Revenu exceptionnel ajouté.');
  // The history lists the newest month first: this one.
  await alice.getByRole('tab', { name: 'Historique' }).click();
  const thisMonth = alice.getByRole('table', { name: 'Versements, mois par mois' }).getByRole('rowgroup').nth(1);
  await expect(thisMonth).toContainText('Revenu exceptionnel');
  await expect(thisMonth).toContainText('400,00 €');

  await alice.getByRole('button', { name: 'Autres actions' }).click();
  await alice.getByRole('menuitem', { name: 'Terminer cette source' }).click();
  await alice.getByRole('dialog', { name: 'Terminer la source' }).getByRole('button', { name: 'Terminer', exact: true }).click();
  const banner = alice.getByRole('note').filter({ hasText: 'Source terminée' });
  await expect(banner).toBeFocused();

  await banner.getByRole('button', { name: 'Rouvrir' }).click();
  await expect(alice.getByText('Source rouverte. Les périodes arrêtées à la fin restent arrêtées : ajoutez la suite si besoin.')).toBeVisible();
  await expect(alice.getByRole('button', { name: 'Autres actions' })).toBeVisible();
});

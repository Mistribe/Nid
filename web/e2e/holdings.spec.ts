import { expect, test } from '@playwright/test';
import { fillDate, pick, toasts } from './controls';
import { signIn } from './session';

test('alice details an assurance-vie by holding, records a snapshot and a cost report', async ({ browser }) => {
  const name = `Assurance-vie ${Date.now()}`;
  const alice = await browser.newPage();
  await signIn(alice, 'alice', '/investments/new');

  await alice.getByLabel('Nom').fill(name);
  await pick(alice, 'Type', 'Assurance-vie');
  await alice.getByRole('radio', { name: 'Privé' }).check();
  await alice.getByRole('button', { name: 'Créer l’enveloppe' }).click();
  await expect(alice.getByRole('heading', { name, level: 1 })).toBeVisible();

  await alice.getByRole('tab', { name: 'Supports' }).click();
  await alice.getByRole('button', { name: 'Détailler les supports' }).click();
  let dialog = alice.getByRole('dialog', { name: 'Nouveau support' });
  await dialog.getByLabel('Support', { exact: true }).fill('Fonds euros');
  await pick(alice, 'Type', 'Fonds en euros');
  await dialog.getByRole('button', { name: 'Ajouter le support' }).click();
  await expect(toasts(alice)).toContainText('Support ajouté.');

  await alice.getByRole('button', { name: 'Ajouter un support' }).click();
  dialog = alice.getByRole('dialog', { name: 'Nouveau support' });
  await dialog.getByLabel('Support', { exact: true }).fill('Monde ESG');
  await dialog.getByRole('button', { name: 'Ajouter le support' }).click();
  await expect(toasts(alice)).toContainText('Support ajouté.');

  await alice.getByRole('button', { name: 'Saisir un relevé' }).click();
  dialog = alice.getByRole('dialog', { name: 'Relevé des supports' });
  await fillDate(dialog, 'Date', '2025-12-31');
  await dialog.getByLabel('Parts de Monde ESG', { exact: true }).fill('10');
  await dialog.getByLabel('Prix unitaire de Monde ESG', { exact: true }).fill('120');
  await dialog.getByLabel('Valeur de Monde ESG', { exact: true }).fill('1200');
  await dialog.getByLabel('Valeur de Fonds euros', { exact: true }).fill('800');
  await dialog.getByLabel('Enregistrer aussi la valeur de l’enveloppe', { exact: true }).check();
  await dialog.getByRole('button', { name: 'Enregistrer le relevé' }).click();
  await expect(toasts(alice)).toContainText('Relevé enregistré.');

  const table = alice.getByRole('table', { name: 'Supports de l’enveloppe' });
  await expect(table.getByRole('row').filter({ hasText: 'Monde ESG' })).toContainText('60 %');
  await expect(alice.getByText('Non réparti')).toHaveCount(0);
  await expect(alice.getByRole('region', { name: 'Valeur et gain' })).toContainText('2 000,00 €');
  await expect(alice.getByRole('region', { name: 'Valeur et gain' })).toContainText('Part UC');

  await table.getByRole('link', { name: 'Monde ESG' }).click();
  await expect(alice.getByRole('heading', { name: 'Monde ESG', level: 1 })).toBeVisible();
  await alice.getByRole('button', { name: 'Autres actions' }).click();
  await alice.getByRole('menuitem', { name: 'Saisir un rapport de frais' }).click();
  dialog = alice.getByRole('dialog', { name: 'Rapport de frais de Monde ESG' });
  await dialog.getByLabel('Année', { exact: true }).fill('2025');
  await dialog.getByLabel('Rendement brut (%)', { exact: true }).fill('8');
  await dialog.getByLabel('Frais totaux (%)', { exact: true }).fill('2,5');
  await dialog.getByLabel('Rendement net (%)', { exact: true }).fill('5,5');
  await dialog.getByRole('button', { name: 'Enregistrer' }).click();
  await expect(toasts(alice)).toContainText('Rapport de frais enregistré.');
  await alice.getByRole('tab', { name: 'Coûts' }).click();
  await expect(alice.getByRole('table', { name: 'Coûts' }).getByRole('row').filter({ hasText: '2025' })).toContainText('2,5 %');
});

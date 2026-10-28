import { expect, test } from '@playwright/test';
import { pick } from './controls';
import { signIn } from './session';

test('alice lands on the dashboard and finds her net worth there', async ({ browser }) => {
  const name = `Livret ${Date.now()}`;
  const alice = await browser.newPage();
  await signIn(alice, 'alice', '/dashboard');
  await expect(alice.getByRole('heading', { name: 'Accueil', level: 1 })).toBeVisible();

  await alice.goto('/investments/new');
  await alice.getByLabel('Nom').fill(name);
  await pick(alice, 'Type', 'Livret');
  await alice.getByRole('radio', { name: /Partagé/ }).check();
  await alice.getByRole('button', { name: 'Première valeur' }).click();
  await alice.getByLabel('Montant de la première valeur').fill('1234');
  await alice.getByRole('button', { name: 'Créer l’enveloppe' }).click();
  await expect(alice.getByRole('heading', { name, level: 1 })).toBeVisible();

  await alice.goto('/');
  await expect(alice).toHaveURL(/\/dashboard$/);
  const worth = alice.getByRole('button', { name: /Patrimoine net/ });
  await expect(worth).toContainText('€');
  await expect(worth).not.toContainText('aucune valeur saisie');

  const curve = alice.getByRole('region', { name: 'Évolution du patrimoine' });
  const toTable = curve.getByRole('button', { name: 'Afficher en tableau' });
  const table = curve.getByRole('table', { name: 'Évolution du patrimoine en tableau' });
  await expect(toTable.or(table)).toBeVisible();
  if (await toTable.isVisible()) await toTable.click();
  const lastRow = table.getByRole('row').last();
  await expect(lastRow).toContainText('Aujourd’hui');
  await expect(lastRow).toContainText('€');

  const projection = alice.getByRole('region', { name: 'Projection du patrimoine' });
  await expect(projection).toBeVisible();
  await projection.getByRole('radio', { name: '20 ans' }).click();
  await expect(alice).toHaveURL(/projection=20y/);
  await projection.getByRole('button', { name: 'Afficher en tableau' }).click();
  const horizonYear = String(new Date().getFullYear() + 20);
  await expect(projection.getByRole('table', { name: 'Projection du patrimoine en tableau' }).getByRole('row', { name: new RegExp(horizonYear) })).toBeVisible();
});

test('alice shares an envelope 50/50 with bob and finds it in the split, keeping Moi through its link', async ({ browser }) => {
  const name = `Commun ${Date.now()}`;
  const alice = await browser.newPage();
  await signIn(alice, 'alice', '/investments/new');

  await alice.getByLabel('Nom').fill(name);
  await pick(alice, 'Type', 'Livret');
  await alice.getByRole('radio', { name: /Partagé/ }).check();
  await alice.getByLabel('Part (%)').first().fill('50');
  await alice.getByRole('button', { name: 'Ajouter un propriétaire' }).click();
  await alice.getByRole('combobox', { name: 'Personne' }).nth(1).click();
  await alice.getByRole('option', { name: 'Bob Dev' }).click();
  await alice.getByLabel('Part (%)').nth(1).fill('50');
  await alice.getByRole('button', { name: 'Première valeur' }).click();
  await alice.getByLabel('Montant de la première valeur').fill('1000');
  await alice.getByRole('button', { name: 'Créer l’enveloppe' }).click();
  await expect(alice.getByRole('heading', { name, level: 1 })).toBeVisible();

  await alice.goto('/dashboard?scope=me');
  await alice.getByRole('button', { name: /Patrimoine net/ }).click();
  const split = alice.getByRole('dialog', { name: 'Patrimoine net' });
  await expect(split.getByRole('row', { name: new RegExp(name) })).toContainText('1 000,00 €');
  await split.getByRole('radio', { name: 'Par personne' }).click();
  await expect(split.getByRole('table', { name: 'Part de chaque personne' })).toContainText('%');
  await split.getByRole('radio', { name: 'Par type' }).click();

  await split.getByRole('link', { name }).click();
  await expect(alice.getByRole('heading', { name, level: 1 })).toBeVisible();
  await expect(alice).toHaveURL(/scope=me/);
});

import { expect, test } from '@playwright/test';
import { signIn } from './session';

test('alice lands on the dashboard and finds her net worth there', async ({ browser }) => {
  const name = `Livret ${Date.now()}`;
  const alice = await browser.newPage();
  await signIn(alice, 'alice', '/dashboard');
  await expect(alice.getByRole('heading', { name: 'Accueil', level: 1 })).toBeVisible();

  await alice.goto('/investments/new');
  await alice.getByLabel('Nom').fill(name);
  await alice.getByLabel('Type').selectOption('savings');
  await alice.getByRole('radio', { name: /Partagé/ }).check();
  await alice.getByRole('checkbox', { name: 'Première valeur' }).check();
  await alice.getByLabel('Montant de la première valeur').fill('1234');
  await alice.getByRole('button', { name: 'Créer l’enveloppe' }).click();
  await expect(alice.getByRole('heading', { name, level: 1 })).toBeVisible();

  await alice.goto('/');
  await expect(alice).toHaveURL(/\/dashboard$/);
  const worth = alice.getByRole('region', { name: 'Patrimoine' });
  await expect(worth).toContainText('€');
  await expect(worth).not.toContainText('Inconnu');
  await expect(alice.getByRole('region', { name: 'Évolution depuis le 1er janvier' })).toBeVisible();
});

import { expect, test } from '@playwright/test';
import { fillDate, pick } from './controls';
import { signIn } from './session';

test('alice simulates a primary home purchase, reads its payment and projection, then deletes it', async ({ browser }) => {
  const stamp = Date.now();
  const envelope = `Livret ${stamp}`;
  const name = `Achat ${stamp}`;
  const alice = await browser.newPage();
  await signIn(alice, 'alice', '/scenarios');

  // The scenario needs an envelope to draw the down payment from and a salary for the ratios.
  const session = (await (await alice.request.get('/auth/session')).json()) as { csrf: string; person: { id: string } };
  const headers = { 'X-Nid-Csrf': session.csrf };
  const owners = [{ personId: session.person.id, shareBp: 10000 }];
  const seeded = await alice.request.post('/api/v1/investments/envelopes', {
    headers, data: { displayName: envelope, kind: 'savings', currency: 'EUR', visibility: 'private', owners, firstValuation: { asOf: '2026-01-01', amount: { amount: '30000', currency: 'EUR' } } },
  });
  expect(seeded.status()).toBe(201);
  const salary = await alice.request.post('/api/v1/income/sources', {
    headers, data: {
      displayName: `Salaire ${stamp}`, kind: 'salary', currency: 'EUR', visibility: 'private', owners,
      phases: [{ label: 'Salaire de base', startsOn: '2025-01-01', netAmount: { amount: '3500', currency: 'EUR' }, every: { count: 1, unit: 'month' } }],
    },
  });
  expect(salary.status()).toBe(201);

  // The seeds are private: only Alice's own scope offers them (the scenario itself is shared, so the household list shows it).
  await alice.goto('/scenarios/new?scope=me');
  await alice.getByLabel('Nom du scénario').fill(name);
  await fillDate(alice, 'Date d’achat', `${new Date().getFullYear() + 1}-06-01`);
  await alice.getByLabel('Prix', { exact: true }).fill('200000');
  await alice.getByLabel('Frais de notaire').fill('15000');
  await alice.getByRole('button', { name: 'Continuer' }).click();

  await alice.getByRole('button', { name: 'Ajouter un apport' }).click();
  await pick(alice, 'Enveloppe', envelope);
  await alice.getByLabel('Montant de l’apport').fill('20000');
  await alice.getByRole('button', { name: 'Continuer' }).click();

  await alice.getByLabel('Montant emprunté').fill('195000');
  await alice.getByLabel('Taux (%)', { exact: true }).fill('3,5');
  await alice.getByLabel('Durée (mois)').fill('240');
  await alice.getByRole('button', { name: 'Continuer' }).click();

  await alice.getByRole('button', { name: 'Créer le scénario' }).click();
  await expect(alice.getByRole('heading', { name, level: 1 })).toBeVisible();
  await expect(alice.getByRole('region', { name: 'Chiffres clés' })).toContainText(/Nouvelle mensualité.*€/s);

  await alice.getByRole('tab', { name: 'Projection' }).click();
  const chart = alice.getByRole('figure', { name: 'Patrimoine net, sans et avec l’achat' });
  await expect(chart.getByText('Sans l’achat')).toBeVisible();
  await expect(chart.getByText('Avec l’achat')).toBeVisible();

  await alice.goto('/scenarios');
  await alice.getByRole('button', { name: `Actions pour ${name}` }).click();
  await alice.getByRole('menuitem', { name: 'Supprimer' }).click();
  await alice.getByRole('alertdialog').getByRole('button', { name: 'Confirmer' }).click();
  await expect(alice.getByRole('link', { name })).toHaveCount(0);
});

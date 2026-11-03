import { expect, test } from '@playwright/test';
import { aMonthAgo, plusMonths } from './dates';
import { fillDate, pick, toasts } from './controls';
import { proposeToken, signIn } from './session';

// 4C2: a token proposes a value; alice sees the badge, approves it, and the envelope shows it.
test('alice approves a value an assistant proposed', async ({ page, playwright }) => {
  const name = `PEA ${Date.now()}`;
  await signIn(page, 'alice', '/investments/new');
  await page.getByLabel('Nom', { exact: true }).fill(name);
  await pick(page, 'Type', 'PEA');
  await page.getByRole('button', { name: 'Créer l’enveloppe' }).click();
  await expect(page.getByRole('heading', { name, level: 1 })).toBeVisible();
  const envelope = page.url().split('/').pop()!;

  const secret = await proposeToken(page);

  const outside = await playwright.request.newContext({ baseURL: 'https://nid.localtest.me', ignoreHTTPSErrors: true });
  const today = new Date().toISOString().slice(0, 10);
  const submitted = await outside.post('/api/v1/core/proposals', {
    headers: { Authorization: `Bearer ${secret}` },
    data: { kind: 'investments.record_valuation', targetItemId: envelope, payload: { asOf: today, amount: { amount: '12345.67', currency: 'EUR' } }, sourceLabel: 'Relevé E2E' },
  });
  expect(submitted.status()).toBe(201);
  await outside.dispose();

  await page.goto('/proposals');
  await expect(page.getByRole('navigation', { name: 'Navigation principale' }).getByRole('link', { name: /À valider/ })).toContainText(/\d/);
  await page.getByRole('table', { name: 'Liste des propositions' }).getByRole('row').filter({ hasText: 'Relevé E2E' }).getByRole('link', { name: new RegExp(name) }).click();
  await expect(page.getByRole('row', { name: /Valeur proposée/ })).toContainText('12 345,67');
  await page.getByRole('button', { name: 'Approuver' }).click();
  await expect(toasts(page)).toContainText('Proposition approuvée.');

  await page.goto(`/investments/${envelope}`);
  await expect(page.getByText('12 345,67', { exact: false }).first()).toBeVisible();
});

// 4D1: a token proposes a statement (a value, two movements); alice leaves one out and approves; the envelope shows the value and one new flow.
test('alice approves a statement an assistant proposed, leaving one movement out', async ({ page, playwright }) => {
  const name = `CTO ${Date.now()}`;
  await signIn(page, 'alice', '/investments/new');
  await page.getByLabel('Nom', { exact: true }).fill(name);
  await pick(page, 'Type', 'PEA');
  await page.getByRole('button', { name: 'Créer l’enveloppe' }).click();
  await expect(page.getByRole('heading', { name, level: 1 })).toBeVisible();
  const envelope = page.url().split('/').pop()!;

  const secret = await proposeToken(page);

  const outside = await playwright.request.newContext({ baseURL: 'https://nid.localtest.me', ignoreHTTPSErrors: true });
  const today = new Date().toISOString().slice(0, 10);
  const submitted = await outside.post('/api/v1/core/proposals', {
    headers: { Authorization: `Bearer ${secret}` },
    data: {
      kind: 'investments.record_statement', targetItemId: envelope, sourceLabel: 'Relevé E2E 4D1',
      payload: {
        asOf: today, value: { amount: '12345.67', currency: 'EUR' },
        movements: [
          { on: today, kind: 'contribution', amount: '111.11', label: 'Versement E2E' },
          { on: today, kind: 'contribution', amount: '222.22', label: 'Doublon E2E' },
        ],
      },
    },
  });
  expect(submitted.status()).toBe(201);
  await outside.dispose();

  await page.goto('/proposals');
  await page.getByRole('table', { name: 'Liste des propositions' }).getByRole('row').filter({ hasText: 'Relevé E2E 4D1' }).getByRole('link', { name: new RegExp(name) }).click();
  await expect(page.getByRole('table', { name: 'Mouvements du relevé' })).toContainText('Doublon E2E');
  await page.getByRole('checkbox', { name: /Mouvement 2/ }).uncheck();
  await page.getByRole('button', { name: 'Approuver' }).click();
  await expect(toasts(page)).toContainText('Proposition approuvée.');

  await page.goto(`/investments/${envelope}`);
  await expect(page.getByText('12 345,67', { exact: false }).first()).toBeVisible();
  await expect(page.getByText('111,11', { exact: false }).first()).toBeVisible();
  await expect(page.getByText('222,22', { exact: false })).toHaveCount(0);
});

// 4D2: a token proposes a loan's bank table (loans.md §4's first two payments); alice keeps « Faire de ce tableau la référence » and approves; the schedule reads the bank's rows.
test('alice approves the bank’s table an assistant proposed, as the loan’s reference', async ({ page, playwright }) => {
  const name = `Prêt ${Date.now()}`;
  const disbursed = aMonthAgo();
  await signIn(page, 'alice', '/loans/new');
  await page.getByLabel('Nom', { exact: true }).fill(name);
  await page.getByRole('radio', { name: 'Non, privé' }).check();
  await page.getByRole('button', { name: 'Continuer' }).click();
  await page.getByLabel('Capital emprunté').fill('200000');
  await page.getByLabel('Taux annuel (%)').fill('3,5');
  await page.getByLabel('Durée (mois)').fill('240');
  await fillDate(page, 'Date de déblocage', disbursed);
  await page.getByRole('button', { name: 'Continuer' }).click();
  await page.getByRole('button', { name: 'Créer le crédit' }).click();
  await expect(page.getByRole('heading', { name, level: 1 })).toBeVisible();
  const loan = page.url().split('/').pop()!;

  const secret = await proposeToken(page);

  const outside = await playwright.request.newContext({ baseURL: 'https://nid.localtest.me', ignoreHTTPSErrors: true });
  const submitted = await outside.post('/api/v1/core/proposals', {
    headers: { Authorization: `Bearer ${secret}` },
    data: {
      kind: 'realestate.import_amortization', targetItemId: loan, sourceLabel: 'Tableau E2E 4D2',
      payload: {
        rows: [
          { dueDate: plusMonths(disbursed, 1), payment: '1159.92', interest: '583.33', principal: '576.59', crd: '199423.41' },
          { dueDate: plusMonths(disbursed, 2), payment: '1159.92', interest: '581.65', principal: '578.27', crd: '198845.14' },
        ],
      },
    },
  });
  expect(submitted.status()).toBe(201);
  await outside.dispose();

  await page.goto('/proposals');
  await page.getByRole('table', { name: 'Liste des propositions' }).getByRole('row').filter({ hasText: 'Tableau E2E 4D2' }).getByRole('link', { name: new RegExp(name) }).click();
  await expect(page.getByText('Les deux concordent (écarts d’arrondi seulement).')).toBeVisible();
  await expect(page.getByRole('checkbox', { name: 'Faire de ce tableau la référence' })).toBeChecked();
  await page.getByRole('button', { name: 'Approuver' }).click();
  await expect(toasts(page)).toContainText('Proposition approuvée.');

  await page.goto(`/loans/${loan}?tab=schedule`);
  await expect(page.getByRole('table', { name: 'Échéancier par année' }).getByText('Banque').first()).toBeVisible();
});

import { readFileSync } from 'node:fs';
import { expect, test, type Page } from '@playwright/test';
import { pick, toasts } from './controls';
import { signIn } from './session';

/** The household's bank sync settings, set through the API: the journey starts from them and leaves them as it found them. */
async function bankSync(page: Page, settings: { bankingEnabled: boolean; enableBankingAppId: string }) {
  const { csrf } = (await (await page.request.get('/auth/session')).json()) as { csrf: string };
  const response = await page.request.patch('/api/v1/admin/household', { headers: { 'X-Nid-Csrf': csrf }, data: settings });
  expect(response.ok()).toBe(true);
}

/**
 * Administration › Banque, then Réglages › Banques on and off. Not the trip to a bank and back: the production host ships no
 * fake Enable Banking and the dev stack holds no credentials; the server's integration tests cover the connection with
 * `FakeEnableBanking`.
 */
test('alice turns bank sync on, names the application, finds Réglages › Banques, and loses it once off', async ({ page }) => {
  await signIn(page, 'alice', '/admin/banking');
  await bankSync(page, { bankingEnabled: false, enableBankingAppId: '' });
  await page.reload();

  const toggle = page.getByRole('switch', { name: 'Synchronisation bancaire' });
  await expect(toggle).not.toBeChecked();
  await toggle.click();
  await expect(toggle).toBeChecked();
  await page.getByLabel('Identifiant de l’application Enable Banking').fill('e2e-application');
  await page.getByRole('button', { name: 'Enregistrer l’identifiant' }).click();
  await expect(toasts(page)).toContainText('Enregistré');
  await expect(page.getByText('Adresse de retour à déclarer : https://nid.localtest.me/api/v1/banking/callback')).toBeVisible();

  await page.goto('/settings/banks');
  await expect(page.getByRole('link', { name: 'Banques', exact: true })).toHaveAttribute('aria-current', 'page');
  // The dev stack has no private key, so bank sync is on but not configured; a stack with one shows alice's empty list.
  const { configured } = (await (await page.request.get('/api/v1/banking/status')).json()) as { configured: boolean };
  await expect(configured ? page.getByRole('heading', { name: 'Aucune banque reliée' }) : page.getByText(/pas encore configurée/)).toBeVisible();

  await page.goto('/admin/banking');
  await page.getByRole('switch', { name: 'Synchronisation bancaire' }).click();
  await expect(page.getByRole('switch', { name: 'Synchronisation bancaire' })).not.toBeChecked();
  await page.goto('/settings/banks');
  await expect(page.getByText(/La synchronisation bancaire n'est pas activée/)).toBeVisible();
  await expect(page.getByRole('link', { name: 'Banques', exact: true })).toHaveCount(0);

  await bankSync(page, { bankingEnabled: false, enableBankingAppId: '' });
});

test('alice keeps the operations 24 months, then without limit', async ({ page }) => {
  await signIn(page, 'alice', '/admin/banking');
  await bankSync(page, { bankingEnabled: true, enableBankingAppId: '' });
  await page.reload();

  await pick(page, 'Conserver les opérations', '24 mois');
  await expect(toasts(page)).toContainText('Enregistré');
  await page.reload();
  await expect(page.getByRole('combobox', { name: 'Conserver les opérations' })).toHaveText('24 mois');
  await pick(page, 'Conserver les opérations', 'Sans limite');
  await expect(toasts(page)).toContainText('Enregistré');
  await page.reload();
  await expect(page.getByRole('combobox', { name: 'Conserver les opérations' })).toHaveText('Sans limite');

  await bankSync(page, { bankingEnabled: false, enableBankingAppId: '' });
});

/** 5C: bank sync on without Enable Banking, alice reaches « Opérations à relier » from « À valider » and keeps no rule yet. */
test('alice opens « Opérations à relier » and finds « Règles mémorisées » empty', async ({ page }) => {
  await signIn(page, 'alice', '/admin/banking');
  await bankSync(page, { bankingEnabled: true, enableBankingAppId: '' });

  await page.goto('/proposals');
  await page.getByRole('link', { name: 'Opérations à relier' }).click();
  await expect(page.getByRole('heading', { level: 1, name: 'Opérations à relier' })).toBeVisible();
  // Empty on a fresh stack; the statement journey below leaves lines to link on a stack that ran it before.
  await expect(page.getByText('Aucune opération à relier.').or(page.getByRole('table', { name: 'Opérations à relier' }))).toBeVisible();

  await page.goto('/settings/banks');
  // No journey links a line by hand, so alice's rules stay empty.
  await expect(page.getByRole('region', { name: 'Règles mémorisées' }).getByText('Aucune règle mémorisée.')).toBeVisible();

  await bankSync(page, { bankingEnabled: false, enableBankingAppId: '' });
});

/** Bank sync on without Enable Banking: a statement file needs nothing else. The vector's account id is made this run's own, so its lines are all new. */
test('alice imports an OFX statement on a checking envelope, previews it, confirms it and finds its lines in « Opérations »', async ({ page }) => {
  const stamp = Date.now();
  const envelope = `Compte ${stamp}`;
  await signIn(page, 'alice', '/admin/banking');
  await bankSync(page, { bankingEnabled: true, enableBankingAppId: '' });
  const session = (await (await page.request.get('/auth/session')).json()) as { csrf: string; person: { id: string } };
  const created = await page.request.post('/api/v1/investments/envelopes', {
    headers: { 'X-Nid-Csrf': session.csrf },
    data: { displayName: envelope, kind: 'checking', currency: 'EUR', visibility: 'private', owners: [{ personId: session.person.id, shareBp: 10000 }],
      firstValuation: { asOf: '2026-01-01', amount: { amount: '1000', currency: 'EUR' } } },
  });
  expect(created.status()).toBe(201);
  const { id } = (await created.json()) as { id: string };
  const ofx = readFileSync(new URL('../../tests/vectors/bankfiles/ofx2-xml.ofx', import.meta.url), 'utf8')
    .replace('<ACCTID>12345678901</ACCTID>', `<ACCTID>${stamp}</ACCTID>`);

  await page.goto(`/investments/${id}?tab=operations`);
  await expect(page.getByText('Aucune opération.')).toBeVisible();
  await page.getByRole('button', { name: 'Importer un relevé' }).click();
  const dialog = page.getByRole('dialog', { name: 'Importer un relevé' });
  await dialog.getByLabel('Relevé (OFX ou CAMT.053)').setInputFiles({ name: 'releve.ofx', mimeType: 'application/x-ofx', buffer: Buffer.from(ofx) });
  await dialog.getByRole('button', { name: 'Lire le relevé' }).click();

  await expect(dialog.getByText('3 nouvelles, 0 déjà connue, 0 probablement déjà connue')).toBeVisible();
  await expect(dialog.getByText('Nouveau compte')).toBeVisible();
  await expect(dialog.getByText('Du 1 octobre 2026 au 28 octobre 2026')).toBeVisible();
  await expect(dialog.getByText('Solde de clôture : 1 234,56 € au 28 octobre 2026')).toBeVisible();
  await dialog.getByRole('button', { name: 'Importer' }).click();

  await expect(dialog).toBeHidden();
  await expect(page.getByText('Relevé importé : 3 opérations ajoutées.')).toBeVisible();
  const row = page.getByRole('table', { name: 'Opérations' }).getByRole('row').filter({ hasText: 'VIR SEPA SOCIETE EXEMPLE SAS' });
  await expect(row).toContainText('2 450,00 €');
  await expect(row).toContainText('27 oct. 2026');

  await bankSync(page, { bankingEnabled: false, enableBankingAppId: '' });
});

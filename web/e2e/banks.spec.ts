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
test('alice turns bank sync on, is sent to her own application, finds Réglages › Banques, and loses it once off', async ({ page }) => {
  await signIn(page, 'alice', '/admin/banking');
  await bankSync(page, { bankingEnabled: false, enableBankingAppId: '' });
  await page.reload();

  const toggle = page.getByRole('switch', { name: 'Synchronisation bancaire' });
  await expect(toggle).not.toBeChecked();
  await toggle.click();
  await expect(toggle).toBeChecked();
  // #414: no installation application for a new setup: each adult declares their own.
  await expect(page.getByLabel('Identifiant de l’application Enable Banking', { exact: true })).toHaveCount(0);
  await page.getByRole('link', { name: 'Ouvrir Mon application' }).click();

  await expect(page.getByRole('link', { name: 'Banques', exact: true })).toHaveAttribute('aria-current', 'page');
  await expect(page.getByRole('heading', { name: 'Mon application Enable Banking' })).toBeFocused();
  // Without an application of her own, the wizard; « Relier une banque » gives way to it.
  await expect(page.getByText('Étape 1 sur 8')).toBeVisible();
  await expect(page.getByRole('link', { name: 'Créer mon application' })).toBeVisible();
  // #413: one page, no second tab row.
  await expect(page.getByRole('tab')).toHaveCount(0);
  await page.getByRole('button', { name: 'Continuer' }).click();
  await expect(page).toHaveURL(/step=2/);
  await expect(page.getByRole('link', { name: 'Ouvrir le portail Enable Banking' })).toBeVisible();

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

/** 5C, #84: bank sync on without Enable Banking, alice reaches the Opérations tab of Dépenses filtered on the lines to link from « À valider » and keeps no rule yet. */
test('alice opens the lines to link in « Opérations » and finds « Règles mémorisées » empty', async ({ page }) => {
  await signIn(page, 'alice', '/admin/banking');
  await bankSync(page, { bankingEnabled: true, enableBankingAppId: '' });

  await page.goto('/proposals');
  await page.getByRole('link', { name: 'Opérations à relier' }).click();
  // #423: Opérations is a tab of Dépenses.
  await expect(page.getByRole('heading', { level: 1, name: 'Dépenses' })).toBeVisible();
  await expect(page.getByRole('tab', { name: 'Opérations', selected: true })).toBeVisible();
  await expect(page).toHaveURL(/\/spending\?/);
  await expect(page).toHaveURL(/state=unlinked/);
  // Empty on a fresh stack; the statement journey below leaves lines to link on a stack that ran it before.
  await expect(page.getByText('Aucune opération à relier.').or(page.getByRole('table', { name: 'Opérations' }))).toBeVisible();

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

/**
 * #449: alice categorises two lines of a shop by hand; Nid then suggests the same category for its third line, and the rule that takes it.
 * The shop's name is this run's own (letters only: a digit ends a suggested pattern), so earlier runs' lines and rules never meet it.
 */
test('alice categorises two lines by hand, sees the suggestion for the third and creates its rule', async ({ page }) => {
  const stamp = Date.now();
  const shop = `FOURNIL ${String(stamp).replace(/\d/g, (d) => 'ABCDEFGHIJ'[Number(d)])}`;
  await signIn(page, 'alice', '/admin/banking');
  await bankSync(page, { bankingEnabled: true, enableBankingAppId: '' });
  const session = (await (await page.request.get('/auth/session')).json()) as { csrf: string; person: { id: string } };
  const created = await page.request.post('/api/v1/investments/envelopes', {
    headers: { 'X-Nid-Csrf': session.csrf },
    data: { displayName: `Compte ${stamp}`, kind: 'checking', currency: 'EUR', visibility: 'private', owners: [{ personId: session.person.id, shareBp: 10000 }],
      firstValuation: { asOf: '2026-01-01', amount: { amount: '1000', currency: 'EUR' } } },
  });
  expect(created.status()).toBe(201);
  const { id } = (await created.json()) as { id: string };
  const line = (day: number) => `<STMTTRN><TRNTYPE>DEBIT</TRNTYPE><DTPOSTED>202610${day}</DTPOSTED><TRNAMT>-4.${day}</TRNAMT><FITID>${stamp}-${day}</FITID><NAME>CB ${shop} ${day}/10</NAME></STMTTRN>`;
  const ofx = `<OFX><BANKMSGSRSV1><STMTTRNRS><STMTRS><CURDEF>EUR</CURDEF><BANKACCTFROM><ACCTID>${stamp}</ACCTID><ACCTTYPE>CHECKING</ACCTTYPE></BANKACCTFROM>`
    + `<BANKTRANLIST><DTSTART>20261001</DTSTART><DTEND>20261028</DTEND>${line(12)}${line(19)}${line(26)}</BANKTRANLIST></STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>`;
  const imported = await page.request.post('/api/v1/banking/statements', {
    headers: { 'X-Nid-Csrf': session.csrf },
    multipart: { file: { name: 'releve.ofx', mimeType: 'application/x-ofx', buffer: Buffer.from(ofx) }, envelopeId: id, confirm: 'true', keepProbablyKnown: 'false' },
  });
  expect(imported.ok()).toBe(true);

  await page.goto(`/spending?tab=operations&q=${encodeURIComponent(shop)}`);
  const table = page.getByRole('table', { name: 'Opérations' });
  await table.getByRole('checkbox', { name: `Sélectionner « CB ${shop} 12/10 »` }).check();
  await table.getByRole('checkbox', { name: `Sélectionner « CB ${shop} 19/10 »` }).check();
  await page.getByRole('button', { name: 'Catégoriser… (2)' }).click();
  const dialog = page.getByRole('dialog', { name: 'Catégoriser 2 opérations' });
  await pick(dialog, 'Catégorie', 'Alimentation › Courses');
  await dialog.getByRole('button', { name: 'Enregistrer' }).click();
  // The dialog offers the rule at once; alice declines it here, the suggestion offers it again.
  await dialog.getByRole('button', { name: 'Non merci' }).click();
  await expect(dialog).toBeHidden();

  const block = page.getByRole('region', { name: /suggestions? de catégorie/ });
  await block.getByRole('button', { name: /^Voir (la|les \d+) suggestions?$/ }).click();
  const suggestion = block.getByRole('listitem').filter({ hasText: `${shop} → Courses` });
  await expect(suggestion).toContainText("1 opération à classer · d'après 2 opérations classées à la main");
  await suggestion.getByRole('button', { name: 'Créer la règle' }).click();
  const offer = page.getByRole('form', { name: `Toujours classer « ${shop} » en Courses\u00a0?` });
  await expect(offer.getByRole('textbox', { name: 'Motif' })).toHaveValue(shop);
  await expect(offer.getByRole('checkbox', { name: 'Appliquer aussi à l’opération passée' })).toBeChecked();
  await offer.getByRole('button', { name: 'Créer la règle' }).click();

  await expect(toasts(page)).toContainText('Règle créée, et 1 opération passée classée.');
  await expect(page.getByRole('listitem').filter({ hasText: `${shop} → Courses` })).toHaveCount(0);
  await expect(table.getByRole('row').filter({ hasText: `CB ${shop} 26/10` })).toContainText('Courses');

  await bankSync(page, { bankingEnabled: false, enableBankingAppId: '' });
});

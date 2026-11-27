import { expect, test, type Page } from '@playwright/test';
import { pick, toasts } from './controls';
import { signIn } from './session';

// deploy/smoke.sh starts the Compose worker and passes its console's code and key fingerprint; without it, nothing to pair.
// The code, then the fingerprint in groups of four as the console prints it.
const [code, ...groups] = (process.env.NID_WORKER_PAIRING ?? '').split(' ').filter(Boolean);
const fingerprint = groups.join(' ');

/**
 * Administration › IA (#476, #585): alice, an admin, pairs the Compose worker by its address, checks its fingerprint against the console, tests it and
 * finds the test's job on the jobs page, asks the assistant a question through it (#528), has its small model guess a line's category (#450);
 * then revokes it, so a later run pairs it again.
 */
test('alice pairs the worker, tests it, finds its job and asks the assistant', async ({ page }) => {
  test.skip(!code || !fingerprint, 'needs NID_WORKER_PAIRING (deploy/smoke.sh)');
  // #450: the guess pass runs every 5 minutes (ModelWorkLoop.GuessEvery); the journey waits for one.
  test.setTimeout(10 * 60_000);
  await signIn(page, 'alice', '/admin/ai');
  // A run that stopped before its revoke left the worker registered here; smoke.sh's `unpair` already reset the worker's side.
  const { csrf } = (await (await page.request.get('/auth/session')).json()) as { csrf: string };
  const { workers } = (await (await page.request.get('/api/v1/ai/workers')).json()) as { workers: { id: string; url: string; status: string }[] };
  for (const left of workers.filter((w) => w.url === 'https://worker:8443' && w.status !== 'revoked'))
    expect((await page.request.post(`/api/v1/ai/workers/${left.id}/revoke`, { headers: { 'X-Nid-Csrf': csrf } })).ok()).toBe(true);
  await page.reload();

  await page.getByRole('button', { name: 'Ajouter un worker' }).first().click();
  const dialog = page.getByRole('dialog', { name: 'Ajouter un worker' });
  await expect(dialog.getByLabel('Adresse du worker')).toHaveValue('https://worker:8443');
  await dialog.getByLabel('Nom', { exact: true }).fill('Worker e2e');
  await dialog.getByRole('button', { name: 'Continuer' }).click();

  await expect(dialog.getByTestId('worker-fingerprint')).toHaveText(fingerprint);
  await dialog.getByLabel("Code d’appairage").fill(code);
  await dialog.getByRole('button', { name: 'Appairer' }).click();
  await expect(toasts(page)).toContainText('Worker e2e est appairé.');

  const row = page.getByRole('row', { name: /Worker e2e/ });
  await expect(row).toContainText('Actif');
  await row.getByRole('button', { name: 'Actions pour Worker e2e' }).click();
  await page.getByRole('menuitem', { name: 'Tester' }).click();
  await expect(toasts(page)).toContainText('Test réussi');

  await page.goto('/admin/ai/jobs?type=echo');
  await expect(page.getByRole('row', { name: /Test d’un worker/ }).first()).toBeVisible();

  // #528: the assistant, end to end, on the scripted engine of compose.local.yaml: the worker declares its model, alice sets it
  // for the chat, and a question goes through the gateway, the tools, the stream and the page.
  const headers = { 'X-Nid-Csrf': csrf };
  await expect.poll(async () => JSON.stringify(await (await page.request.get('/api/v1/ai/models')).json())).toContain('nid-test-model');
  expect((await page.request.put('/api/v1/ai/settings/uses/chat/model', { headers, data: { model: 'nid-test-model' } })).ok()).toBe(true);
  // #533: each adult turns the assistant on for themselves.
  expect((await page.request.put('/api/v1/ai/assistant/me/enabled', { headers, data: { enabled: true } })).ok()).toBe(true);
  const session = (await (await page.request.get('/auth/session')).json()) as { person: { id: string } };
  expect((await page.request.post('/api/v1/investments/envelopes', {
    headers,
    data: {
      displayName: `Assistant e2e ${Date.now()}`, kind: 'savings', currency: 'EUR', visibility: 'shared',
      owners: [{ personId: session.person.id, shareBp: 10000 }], firstValuation: { asOf: '2026-01-01', amount: { amount: '1000', currency: 'EUR' } },
    },
  })).status()).toBe(201);

  // #534: from any page, through the top bar's panel.
  await page.goto('/dashboard');
  await page.getByRole('button', { name: 'Assistant' }).click();
  const panel = page.getByRole('dialog', { name: 'Assistant' });
  await panel.getByRole('textbox', { name: 'Votre question' }).fill('Quelles enveloppes ?');
  await panel.getByRole('button', { name: 'Demander' }).click();
  const answer = panel.getByText(/D'après Nid, votre enveloppe « .+ » est suivie/);
  await expect(answer).toBeVisible();
  await expect(panel.getByText('Consulté : list_envelopes')).toBeVisible();
  await expect(panel.getByText('Répondu par Worker e2e, modèle nid-test-model.')).toBeVisible();
  const named = /« (.+) »/.exec((await answer.textContent()) ?? '')![1];
  await expect(panel.getByRole('list', { name: 'Pages citées' }).getByRole('link', { name: named })).toBeVisible();
  await expect(panel.getByRole('link', { name: /example\.test/ })).toHaveCount(0); // the model's URL stays text
  expect((await page.request.put('/api/v1/ai/settings/uses/chat/model', { headers, data: { model: null } })).ok()).toBe(true);

  await guessesACategory(page, headers);

  await page.goto('/admin/ai');
  await page.getByRole('row', { name: /Worker e2e/ }).getByRole('button', { name: 'Actions pour Worker e2e' }).click();
  await page.getByRole('menuitem', { name: 'Révoquer' }).click();
  await page.getByRole('alertdialog').getByRole('button', { name: 'Confirmer' }).click();
  await expect(toasts(page)).toContainText('Worker e2e révoqué.');
});

/**
 * #450: alice sets the worker's `nid-test-decide` for « Suggestions de catégories » and calibrates it (deploy/local/fake-engine.mjs is right
 * often enough on the evaluation set); a line of a pharmacy nobody classified then gets a category « deviné par IA », which she accepts. The
 * shop's name is this run's own (letters only), so no earlier run's line or choice decides it first.
 */
async function guessesACategory(page: Page, headers: Record<string, string>) {
  const stamp = Date.now();
  const shop = `PHARMACIE ${String(stamp).replace(/\d/g, (d) => 'ABCDEFGHIJ'[Number(d)])}`;
  // As banks.spec.ts: bank sync on for the journey, off again after. Asked again before each read: banks.spec.ts turns it off between its journeys.
  const bankSync = async (on: boolean) =>
    expect((await page.request.patch('/api/v1/admin/household', { headers, data: { bankingEnabled: on, enableBankingAppId: '' } })).ok()).toBe(true);
  await bankSync(true);
  const session = (await (await page.request.get('/auth/session')).json()) as { person: { id: string } };
  const created = await page.request.post('/api/v1/investments/envelopes', {
    headers,
    data: { displayName: `Compte ${stamp}`, kind: 'checking', currency: 'EUR', visibility: 'private', owners: [{ personId: session.person.id, shareBp: 10000 }],
      firstValuation: { asOf: '2026-01-01', amount: { amount: '1000', currency: 'EUR' } } },
  });
  expect(created.status()).toBe(201);
  const { id } = (await created.json()) as { id: string };
  const ofx = `<OFX><BANKMSGSRSV1><STMTTRNRS><STMTRS><CURDEF>EUR</CURDEF><BANKACCTFROM><ACCTID>${stamp}</ACCTID><ACCTTYPE>CHECKING</ACCTTYPE></BANKACCTFROM>`
    + `<BANKTRANLIST><DTSTART>20261101</DTSTART><DTEND>20261126</DTEND><STMTTRN><TRNTYPE>DEBIT</TRNTYPE><DTPOSTED>20261124</DTPOSTED><TRNAMT>-12.40</TRNAMT>`
    + `<FITID>${stamp}-1</FITID><NAME>CB ${shop} 24/11</NAME></STMTTRN></BANKTRANLIST></STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>`;
  expect((await page.request.post('/api/v1/banking/statements', {
    headers,
    multipart: { file: { name: 'releve.ofx', mimeType: 'application/x-ofx', buffer: Buffer.from(ofx) }, envelopeId: id, confirm: 'true', keepProbablyKnown: 'false' },
  })).ok()).toBe(true);

  await page.goto('/admin/ai');
  const row = page.getByRole('heading', { name: 'Suggestions de catégories' }).locator('..');
  await expect(row).toContainText('Pas encore calibré'); // by meaning, on a stack whose search model was never calibrated
  await pick(row, 'Modèle', 'nid-test-decide (1 worker)');
  await expect(toasts(page)).toContainText('Réglage enregistré.');
  await expect(row).toContainText('Deviné par IA, avec nid-test-decide.');
  await row.getByRole('button', { name: 'Calibrer' }).click();
  await expect(toasts(page)).toContainText('Calibrage demandé');
  // The calibration pass runs every 30 seconds; the page reads its state again until it ends.
  await expect(row.getByRole('status')).toHaveText(/^Calibré le/, { timeout: 120_000 });
  await expect(row).toContainText('Sur 412 libellés d’essai');
  await expect(row).not.toContainText('Pas encore calibré');

  // The guess pass: every 5 minutes. The largest groups come first: a one-line group may be on a later page.
  let onPage = 1;
  const guessed = async () => {
    await bankSync(true);
    for (onPage = 1; ; onPage++) {
      const { items, total } = (await (await page.request.get(`/api/v1/banking/category-suggestions?page=${onPage}`)).json()) as
        { items: { pattern: string; basis: string; categoryName: string }[]; total: number };
      const found = items.find((s) => s.pattern === shop);
      if (found) return `${found.basis} ${found.categoryName}`;
      if (items.length === 0 || onPage * 10 >= total) return null;
    }
  };
  await expect.poll(guessed, { timeout: 6 * 60_000, intervals: [10_000] }).toBe('ai Médecin & pharmacie');

  await expect(async () => {
    await bankSync(true);
    await page.goto('/spending?tab=operations');
    const block = page.getByRole('region', { name: /suggestions? de catégorie/ });
    await block.getByRole('button', { name: /^Voir (la|les \d+) suggestions?$/ }).click({ timeout: 5_000 });
    for (let at = 1; at < onPage; at++) {
      await block.getByRole('button', { name: 'Suivant' }).click({ timeout: 5_000 });
      await expect(block).toContainText(`Page ${at + 1} sur`, { timeout: 5_000 });
    }
    const suggestion = block.getByRole('listitem').filter({ hasText: `${shop} → Médecin & pharmacie` });
    await expect(suggestion).toContainText('deviné par IA (modèle local)', { timeout: 5_000 });
    await suggestion.getByRole('button', { name: 'Classer cette opération' }).click({ timeout: 5_000 });
  }).toPass({ timeout: 60_000 });
  await expect(toasts(page)).toContainText('1 opération classée en Médecin & pharmacie.');

  expect((await page.request.put('/api/v1/ai/settings/uses/decide/model', { headers, data: { model: null } })).ok()).toBe(true);
  await bankSync(false);
}

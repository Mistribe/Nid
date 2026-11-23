import { expect, test } from '@playwright/test';
import { toasts } from './controls';
import { signIn } from './session';

// deploy/smoke.sh starts the Compose worker and passes its console's code and key fingerprint; without it, nothing to pair.
// The code, then the fingerprint in groups of four as the console prints it.
const [code, ...groups] = (process.env.NID_WORKER_PAIRING ?? '').split(' ').filter(Boolean);
const fingerprint = groups.join(' ');

/**
 * Réglages › IA (#476): alice pairs the Compose worker by its address, checks its fingerprint against the console, tests it and
 * finds the test's job on the jobs page, asks the assistant a question through it (#528); then revokes it, so a later run pairs it again.
 */
test('alice pairs the worker, tests it, finds its job and asks the assistant', async ({ page }) => {
  test.skip(!code || !fingerprint, 'needs NID_WORKER_PAIRING (deploy/smoke.sh)');
  await signIn(page, 'alice', '/settings/ai');
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
  await dialog.getByLabel("Code d'appairage").fill(code);
  await dialog.getByRole('button', { name: 'Appairer' }).click();
  await expect(toasts(page)).toContainText('Worker e2e est appairé.');

  const row = page.getByRole('row', { name: /Worker e2e/ });
  await expect(row).toContainText('Actif');
  await row.getByRole('button', { name: 'Actions pour Worker e2e' }).click();
  await page.getByRole('menuitem', { name: 'Tester' }).click();
  await expect(toasts(page)).toContainText('Test réussi');

  await page.goto('/settings/ai/jobs?type=echo');
  await expect(page.getByRole('row', { name: /Test d'un worker/ }).first()).toBeVisible();

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

  await page.goto('/settings/ai/assistant');
  await page.getByRole('textbox', { name: 'Votre question' }).fill('Quelles enveloppes ?');
  await page.getByRole('button', { name: 'Demander' }).click();
  const answer = page.getByText(/D'après Nid, votre enveloppe « .+ » est suivie/);
  await expect(answer).toBeVisible();
  await expect(page.getByText('Consulté : list_envelopes')).toBeVisible();
  await expect(page.getByText('Répondu par Worker e2e, modèle nid-test-model.')).toBeVisible();
  const named = /« (.+) »/.exec((await answer.textContent()) ?? '')![1];
  await expect(page.getByRole('list', { name: 'Pages citées' }).getByRole('link', { name: named })).toBeVisible();
  await expect(page.getByRole('link', { name: /example\.test/ })).toHaveCount(0); // the model's URL stays text
  expect((await page.request.put('/api/v1/ai/settings/uses/chat/model', { headers, data: { model: null } })).ok()).toBe(true);

  await page.goto('/settings/ai');
  await page.getByRole('row', { name: /Worker e2e/ }).getByRole('button', { name: 'Actions pour Worker e2e' }).click();
  await page.getByRole('menuitem', { name: 'Révoquer' }).click();
  await page.getByRole('alertdialog').getByRole('button', { name: 'Confirmer' }).click();
  await expect(toasts(page)).toContainText('Worker e2e révoqué.');
});

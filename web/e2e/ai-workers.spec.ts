import { expect, test } from '@playwright/test';
import { toasts } from './controls';
import { signIn } from './session';

// deploy/smoke.sh starts the Compose worker and passes its console's code and key fingerprint; without it, nothing to pair.
// The code, then the fingerprint in groups of four as the console prints it.
const [code, ...groups] = (process.env.NID_WORKER_PAIRING ?? '').split(' ').filter(Boolean);
const fingerprint = groups.join(' ');

/**
 * Réglages › IA (#476): alice pairs the Compose worker by its address, checks its fingerprint against the console, tests it and
 * finds the test's job on the jobs page; then revokes it, so a later run pairs it again.
 */
test('alice pairs the worker, tests it and finds its job', async ({ page }) => {
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

  await page.goto('/settings/ai');
  await page.getByRole('row', { name: /Worker e2e/ }).getByRole('button', { name: 'Actions pour Worker e2e' }).click();
  await page.getByRole('menuitem', { name: 'Révoquer' }).click();
  await page.getByRole('alertdialog').getByRole('button', { name: 'Confirmer' }).click();
  await expect(toasts(page)).toContainText('Worker e2e révoqué.');
});

import { expect, test, type Page } from '@playwright/test';
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
  await expect(page.getByRole('status')).toHaveText('Enregistré');
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

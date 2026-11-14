import { expect, test } from '@playwright/test';
import { ALICE, markGuidesSeen, passwordField } from './accounts';

// local-accounts.md §5, §18.2: an empty base; the setup address comes from the container's log (smoke-local.sh reads it).
test('the setup address of the log creates the first administrator, who then signs in', async ({ page }) => {
  const setupUrl = process.env.NID_SETUP_URL;
  if (!setupUrl) throw new Error('NID_SETUP_URL: the address Nid wrote to its log at the first start (deploy/standalone/smoke-local.sh).');

  await page.goto(setupUrl);
  await page.getByLabel('Votre prénom').fill('Alice');
  await page.getByLabel('Identifiant').fill(ALICE.login);
  await passwordField(page).fill(ALICE.password);
  await page.getByRole('button', { name: 'Créer le compte administrateur' }).click();

  // The setup opens no session: the login page, the identifier filled in.
  await page.waitForURL((url) => url.pathname === '/login');
  await expect(page.getByLabel('Identifiant')).toHaveValue(ALICE.login);
  await passwordField(page).fill(ALICE.password);
  await page.getByRole('button', { name: 'Se connecter' }).click();
  await page.waitForURL((url) => url.pathname !== '/login');
  await markGuidesSeen(page);
  await page.goto('/dashboard');
  await expect(page.getByRole('heading', { level: 1 })).toBeVisible();

  // Once set up, the address does not work any more.
  const again = await page.context().browser()!.newPage();
  await again.goto(setupUrl);
  await expect(again.getByText('Nid est déjà en place.')).toBeVisible();
});

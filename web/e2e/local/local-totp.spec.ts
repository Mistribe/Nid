import { expect, test } from '@playwright/test';
import { BOB, BOB_RESET, newPage, passwordField, signInWithPassword, totpCode } from './accounts';

// local-accounts.md §10, §18.2: Bob turns two-step verification on, then signs in with a code, then with a recovery code.
test('two-step verification asks for the app code, or a recovery code once', async ({ page, browser }) => {
  await signInWithPassword(page, BOB.login, BOB_RESET, '/security');
  await expect(page.getByRole('heading', { name: 'Mot de passe' })).toBeVisible();
  const section = page.getByRole('region', { name: 'Validation en deux étapes' });
  await section.getByRole('button', { name: 'Activer' }).click();

  const setup = page.getByRole('dialog', { name: 'Activer la validation en deux étapes' });
  await expect(setup.getByRole('img', { name: /QR code/ })).toBeVisible();
  const key = (await setup.getByTestId('totp-secret').textContent())!.replace(/\s/g, '');
  await setup.getByLabel('Code', { exact: true }).fill(totpCode(key));
  await setup.getByRole('button', { name: 'Activer' }).click();

  const shown = page.getByRole('dialog', { name: 'Vos codes de secours' });
  await expect(shown.getByRole('listitem')).toHaveCount(10);
  const codes = await shown.getByRole('listitem').allTextContents();
  await shown.getByRole('button', { name: 'J\'ai noté mes codes' }).click();
  await expect(section).toContainText('Codes de secours restants : 10.');

  // The code the activation used is spent: the next step's, within the drift allowed.
  const phone = await newPage(browser);
  await signInWithPassword(phone, BOB.login, BOB_RESET, '/dashboard', () => totpCode(key, 1));

  const laptop = await newPage(browser);
  await laptop.goto('/login?returnUrl=%2Fsecurity');
  await laptop.getByLabel('Identifiant').fill(BOB.login);
  await passwordField(laptop).fill(BOB_RESET);
  await laptop.getByRole('button', { name: 'Se connecter' }).click();
  await laptop.getByRole('button', { name: 'Utiliser un code de secours' }).click();
  await laptop.getByLabel('Code de secours').fill(codes[0]);
  await laptop.getByRole('button', { name: 'Valider' }).click();
  await laptop.waitForURL((url) => url.pathname === '/security');
  await expect(laptop.getByRole('region', { name: 'Validation en deux étapes' })).toContainText('Codes de secours restants : 9.');
});

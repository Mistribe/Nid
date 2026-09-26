import { expect, test } from '@playwright/test';
import { signIn } from './session';

// 1F3a spec §9 test 9: sign in, see it, sign out everywhere, land signed out.
test('an adult sees their sign-in and signs out everywhere', async ({ page }) => {
  await signIn(page, 'bob', '/security');

  await expect(page.getByRole('row', { name: /Connexion/ }).first()).toBeVisible();

  await page.getByRole('button', { name: 'Déconnecter partout' }).click();
  await page.getByRole('button', { name: 'Confirmer' }).click();

  await expect(page.getByRole('heading', { name: 'Vous êtes déconnecté de Nid' })).toBeVisible();
});

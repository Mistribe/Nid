import { expect, test, type Page } from '@playwright/test';
import { ALICE, signInWithPassword } from './accounts';

// « Actions d’Alice », « Actions de Bob »: the menu's name elides before a vowel.
const menuOf = (name: string) => (/^[aeiouy]/i.test(name) ? `Actions d’${name}` : `Actions de ${name}`);

async function fromMenu(page: Page, name: string, item: string): Promise<void> {
  await page.getByRole('button', { name: menuOf(name) }).click();
  await page.getByRole('menuitem', { name: item }).click();
  await page.getByRole('alertdialog').getByRole('button', { name: 'Confirmer' }).click();
}

// local-accounts.md §6, §18.2: the admin role is set in Nid; the last administrator keeps it.
test('the admin role is given and taken back in Nid, and the last administrator keeps it', async ({ page }) => {
  await signInWithPassword(page, ALICE.login, ALICE.password, '/admin/users');
  const bob = page.getByRole('row', { name: /Bob/ });

  await fromMenu(page, 'Bob', 'Donner le rôle admin');
  await expect(bob).toContainText('Admin');
  await fromMenu(page, 'Bob', 'Retirer le rôle admin');
  await expect(bob).not.toContainText('Admin');

  // Alone with the role, Alice cannot take it from herself.
  await fromMenu(page, 'Alice', 'Retirer le rôle admin');
  await expect(page.getByText(/dernier administrateur du foyer/)).toBeVisible();
  await expect(page.getByRole('row', { name: /Alice/ })).toContainText('Admin');
});

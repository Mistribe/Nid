import { expect, test } from '@playwright/test';
import { signIn } from './session';

/** A calendar date a month before today (yyyy-mm-dd), its day kept below 29 so the month after has it. */
function aMonthAgo(): string {
  const now = new Date();
  return new Date(Date.UTC(now.getFullYear(), now.getMonth() - 1, Math.min(now.getDate(), 28))).toISOString().slice(0, 10);
}

test('alice creates a private loan and reads its schedule; bob never sees it', async ({ browser }) => {
  const name = `Prêt ${Date.now()}`;
  const alice = await browser.newPage();
  await signIn(alice, 'alice', '/loans/new');

  await alice.getByLabel('Nom', { exact: true }).fill(name);
  await alice.getByRole('radio', { name: 'Non, privé' }).check();
  await alice.getByLabel('Capital emprunté').fill('200000');
  await alice.getByLabel('Taux annuel (%)').fill('3,5');
  await alice.getByLabel('Durée (mois)').fill('240');
  await alice.getByLabel('Date de déblocage').fill(aMonthAgo());
  await alice.getByRole('button', { name: 'Créer le crédit' }).click();

  await expect(alice.getByRole('heading', { name, level: 1 })).toBeVisible();
  const url = alice.url();
  // The first payment is a month after the disbursement, so this year has payments: it is the one open.
  const schedule = alice.getByRole('table', { name: 'Échéancier par année' });
  await expect(schedule.getByRole('button', { name: `Année ${new Date().getFullYear()}` })).toHaveAttribute('aria-expanded', 'true');

  await alice.goto('/loans?scope=me');
  await expect(alice.getByRole('listitem').filter({ hasText: name })).toContainText('1 159,92 €');

  const bob = await browser.newPage();
  await signIn(bob, 'bob', '/loans');
  await expect(bob.getByRole('heading', { name: 'Crédits', level: 1 })).toBeVisible();
  // The heading renders while the list is still a skeleton (aria-busy): wait for its answer before asserting absence.
  await expect(bob.locator('[aria-busy="true"]')).toHaveCount(0);
  await expect(bob.getByText(name)).toHaveCount(0);
  await bob.goto(url);
  await expect(bob.getByText('Cet élément est introuvable.')).toBeVisible();
});

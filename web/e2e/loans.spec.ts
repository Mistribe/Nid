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
  // The year of the next payment is the one open, and that payment is marked.
  const schedule = alice.getByRole('table', { name: 'Échéancier par année' });
  await expect(schedule.getByRole('button', { expanded: true })).toHaveCount(1);
  await expect(schedule.getByText('prochaine échéance')).toBeVisible();

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

test('alice simulates an early repayment, records it from the simulation, then changes her contract’s rate', async ({ browser }) => {
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

  const nextPayment = alice.getByRole('region', { name: 'Aujourd’hui' }).locator('dt:text-is("Prochaine échéance") + dd');
  await expect(nextPayment).toContainText('1 159,92 €');

  // Simulated today: the repayment applies after the payment due on or before today, so the next payment is after it.
  const simulation = alice.getByRole('region', { name: 'Simuler un remboursement anticipé' });
  await simulation.getByLabel('Montant à rembourser').fill('20000');
  await simulation.getByRole('button', { name: 'Simuler' }).click();
  await expect(simulation.getByRole('table', { name: 'Résultat de la simulation' })).toBeVisible();
  await simulation.getByRole('button', { name: 'Enregistrer ce remboursement en réduisant la mensualité' }).click();

  const repayments = alice.getByRole('region', { name: 'Remboursements anticipés' });
  const form = repayments.getByRole('form', { name: 'Nouveau remboursement anticipé' });
  await expect(form.getByLabel('Montant')).toHaveValue('20000');
  await expect(form.getByRole('radio', { name: 'Réduire la mensualité' })).toBeChecked();
  await form.getByRole('button', { name: 'Ajouter' }).click();
  await expect(repayments.getByRole('status')).toHaveText('Remboursement anticipé ajouté.');
  await expect(repayments.getByRole('table', { name: 'Remboursements anticipés du crédit' })).toContainText('20 000,00 €');
  // Reducing the payment: the next one is no longer 1 159,92 €.
  await expect(nextPayment).not.toContainText('1 159,92 €');
  const reduced = (await nextPayment.textContent()) ?? '';

  await alice.getByRole('link', { name: 'Modifier le contrat' }).click();
  const rate = alice.getByLabel('Taux annuel (%)');
  await expect(rate).toHaveValue('3,5');
  await rate.fill('3');
  await alice.getByRole('button', { name: 'Enregistrer le contrat' }).click();

  await expect(alice.getByRole('heading', { name, level: 1 })).toBeVisible();
  await expect(alice.getByText('Contrat enregistré.')).toBeVisible();
  await expect(nextPayment).not.toHaveText(reduced);
});

import { expect, test } from '@playwright/test';
import { fillDate } from './controls';
import { aMonthAgo, plusMonths } from './dates';
import { signIn } from './session';

/** yyyy-mm-dd as a French spreadsheet writes it: dd/mm/yyyy. */
const french = (iso: string) => `${iso.slice(8, 10)}/${iso.slice(5, 7)}/${iso.slice(0, 4)}`;

test('alice creates a private loan and reads its schedule; bob never sees it', async ({ browser }) => {
  const name = `Prêt ${Date.now()}`;
  const alice = await browser.newPage();
  await signIn(alice, 'alice', '/loans/new');

  await alice.getByLabel('Nom', { exact: true }).fill(name);
  await alice.getByRole('radio', { name: 'Non, privé' }).check();
  await alice.getByRole('button', { name: 'Continuer' }).click();
  await alice.getByLabel('Capital emprunté').fill('200000');
  await alice.getByLabel('Taux annuel (%)').fill('3,5');
  await alice.getByLabel('Durée (mois)').fill('240');
  await fillDate(alice, 'Date de déblocage', aMonthAgo());
  await alice.getByRole('button', { name: 'Continuer' }).click();
  await alice.getByRole('button', { name: 'Créer le crédit' }).click();

  await expect(alice.getByRole('heading', { name, level: 1 })).toBeVisible();
  const url = alice.url();
  // The year of the next payment is the one open, and that payment is marked.
  await alice.getByRole('tab', { name: 'Échéancier' }).click();
  const schedule = alice.getByRole('table', { name: 'Échéancier par année' });
  await expect(schedule.getByRole('button', { expanded: true })).toHaveCount(1);
  await expect(schedule.getByText('prochaine échéance')).toBeVisible();

  await alice.goto('/loans?scope=me');
  await expect(alice.getByRole('table', { name: 'Liste des crédits' }).getByRole('row').filter({ hasText: name })).toContainText('1 159,92 €');

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
  await alice.getByRole('button', { name: 'Continuer' }).click();
  await alice.getByLabel('Capital emprunté').fill('200000');
  await alice.getByLabel('Taux annuel (%)').fill('3,5');
  await alice.getByLabel('Durée (mois)').fill('240');
  await fillDate(alice, 'Date de déblocage', aMonthAgo());
  await alice.getByRole('button', { name: 'Continuer' }).click();
  await alice.getByRole('button', { name: 'Créer le crédit' }).click();
  await expect(alice.getByRole('heading', { name, level: 1 })).toBeVisible();

  const amount = /\d,\d{2}\D*€/;
  const nextPayment = alice.getByRole('region', { name: 'Aujourd’hui' }).locator('dt:text-is("Prochaine échéance") + dd');
  await expect(nextPayment).toContainText('1 159,92 €');

  // Simulated today: the repayment applies after the payment due on or before today, so the next payment is after it.
  await alice.getByRole('button', { name: 'Simuler un remboursement' }).click();
  const simulation = alice.getByRole('region', { name: 'Simuler un remboursement anticipé' });
  await simulation.getByLabel('Montant à rembourser').fill('20000');
  await simulation.getByRole('button', { name: 'Simuler' }).click();
  await expect(simulation.getByRole('table', { name: 'Résultat de la simulation' })).toBeVisible();
  await simulation.getByRole('button', { name: 'Enregistrer ce remboursement en réduisant la mensualité' }).click();

  const repayments = alice.getByRole('region', { name: 'Remboursements anticipés' });
  const form = alice.getByRole('dialog', { name: 'Nouveau remboursement anticipé' });
  await expect(form.getByLabel('Montant')).toHaveValue('20000');
  await expect(form.getByRole('radio', { name: 'Réduire la mensualité' })).toBeChecked();
  await form.getByRole('button', { name: 'Ajouter' }).click();
  await expect(repayments.getByRole('status')).toHaveText('Remboursement anticipé ajouté.');
  await expect(repayments.getByRole('table', { name: 'Remboursements anticipés du crédit' })).toContainText('20 000,00 €');
  // Reducing the payment: the next one is no longer 1 159,92 €.
  // Wait for an amount first: a negated assertion also passes while the figure is absent.
  await expect(nextPayment).toContainText(amount);
  await expect(nextPayment).not.toContainText('1 159,92 €');
  const reduced = (await nextPayment.textContent()) ?? '';
  expect(reduced).toMatch(amount);

  await alice.getByRole('tab', { name: 'Contrat et réglages' }).click();
  await alice.getByRole('link', { name: 'Modifier le contrat' }).click();
  const rate = alice.getByLabel('Taux annuel (%)');
  await expect(rate).toHaveValue('3,5');
  await rate.fill('3');
  await alice.getByRole('button', { name: 'Enregistrer le contrat' }).click();

  await expect(alice.getByRole('heading', { name, level: 1 })).toBeVisible();
  await expect(alice.getByText('Contrat enregistré.')).toBeVisible();
  await expect(alice.locator('[aria-busy="true"]')).toHaveCount(0);
  await expect(nextPayment).toContainText(amount);
  await expect(nextPayment).not.toHaveText(reduced);
});

test('alice pastes her bank’s table, reads the comparison and takes the bank as the reference', async ({ browser }) => {
  const name = `Prêt ${Date.now()}`;
  const disbursed = aMonthAgo();
  const alice = await browser.newPage();
  await signIn(alice, 'alice', '/loans/new');

  await alice.getByLabel('Nom', { exact: true }).fill(name);
  await alice.getByRole('radio', { name: 'Non, privé' }).check();
  await alice.getByRole('button', { name: 'Continuer' }).click();
  await alice.getByLabel('Capital emprunté').fill('200000');
  await alice.getByLabel('Taux annuel (%)').fill('3,5');
  await alice.getByLabel('Durée (mois)').fill('240');
  await fillDate(alice, 'Date de déblocage', disbursed);
  await alice.getByRole('button', { name: 'Continuer' }).click();
  await alice.getByRole('button', { name: 'Créer le crédit' }).click();
  await expect(alice.getByRole('heading', { name, level: 1 })).toBeVisible();

  // loans.md §4's first two payments as the bank's spreadsheet gives them: a header, semicolons, decimal commas.
  const table = [
    'Date;Échéance;Intérêts;Capital;Capital restant dû',
    `${french(plusMonths(disbursed, 1))};1 159,92;583,33;576,59;199 423,41`,
    `${french(plusMonths(disbursed, 2))};1 159,92;581,65;578,27;198 845,14`,
  ].join('\n');
  await alice.getByRole('tab', { name: 'Échéancier' }).click();
  const bank = alice.getByRole('region', { name: 'Tableau de la banque' });
  await expect(bank.getByText('Aucun tableau de la banque enregistré.')).toBeVisible();
  await bank.getByRole('button', { name: 'Importer le tableau de la banque' }).click();
  await bank.getByLabel('Tableau copié').fill(table);
  await bank.getByRole('button', { name: 'Comparer' }).click();
  await expect(bank.getByText('Les deux concordent (écarts d’arrondi seulement).')).toBeVisible();
  await expect(bank.getByRole('table', { name: 'Lignes lues' }).getByRole('row')).toHaveCount(3);
  await bank.getByRole('button', { name: 'Enregistrer le tableau' }).click();
  await expect(bank.getByRole('status')).toHaveText('Tableau de la banque enregistré.');

  await bank.getByRole('radio', { name: 'Tableau de la banque' }).check();
  await bank.getByRole('button', { name: 'Choisir cette référence' }).click();
  await expect(bank.getByRole('status')).toHaveText('Le tableau de la banque est la référence.');
  // The open year holds the next payment, one of the two the bank's table gives: its row says so.
  await expect(alice.getByRole('table', { name: 'Échéancier par année' }).getByText('Banque').first()).toBeVisible();
});

import { expect, test, type Page } from '@playwright/test';
import { signIn } from './session';

/** README §12's purchase through the form — 250 000 € and 19 000 € of notary fees on 2020-06-15 —, landing on its detail. */
async function createProperty(page: Page, name: string, visibility: 'Oui, partagé' | 'Non, privé'): Promise<string> {
  await page.goto('/properties/new');
  await page.getByLabel('Nom', { exact: true }).fill(name);
  await page.getByRole('radio', { name: visibility }).check();
  await page.getByLabel('Date d’achat').fill('2020-06-15');
  await page.getByLabel('Prix d’achat').fill('250000');
  await page.getByLabel('Frais de notaire').fill('19000');
  await page.getByRole('button', { name: 'Créer le bien' }).click();
  await expect(page.getByRole('heading', { name, level: 1 })).toBeVisible();
  return page.url();
}

test('alice records a shared property, its estimate, its works and her loan; bob reads it, never her private one', async ({ browser }) => {
  const stamp = Date.now();
  const home = `Maison ${stamp}`;
  const alice = await browser.newPage();
  await signIn(alice, 'alice', '/properties');
  await createProperty(alice, home, 'Oui, partagé');

  const estimates = alice.getByRole('region', { name: 'Estimations' });
  await estimates.getByRole('button', { name: 'Ajouter une estimation' }).click();
  const estimate = estimates.getByRole('form', { name: 'Nouvelle estimation' });
  await estimate.getByLabel('Valeur').fill('290000');
  await estimate.getByRole('button', { name: 'Ajouter' }).click();
  await expect(estimates.getByRole('status')).toHaveText('Estimation ajoutée.');

  const flows = alice.getByRole('region', { name: 'Dépenses et revenus' });
  await flows.getByRole('button', { name: 'Ajouter une dépense ou un revenu' }).click();
  const flow = flows.getByRole('form', { name: 'Nouvelle dépense ou nouveau revenu' });
  await expect(flow.getByLabel('Catégorie')).toHaveValue('works');
  await flow.getByLabel('Montant').fill('12000');
  await flow.getByRole('button', { name: 'Ajouter' }).click();
  await expect(flows.getByRole('status')).toHaveText('Ligne ajoutée.');

  // Her loan, private, disbursed today: nothing is repaid yet, so its balance is the 200 000 € borrowed.
  const loan = `Prêt ${stamp}`;
  await alice.goto('/loans/new');
  await alice.getByLabel('Nom', { exact: true }).fill(loan);
  await alice.getByRole('radio', { name: 'Non, privé' }).check();
  await alice.getByLabel('Bien financé').selectOption({ label: home });
  await alice.getByLabel('Capital emprunté').fill('200000');
  await alice.getByLabel('Taux annuel (%)').fill('3,5');
  await alice.getByLabel('Durée (mois)').fill('240');
  await alice.getByRole('button', { name: 'Créer le crédit' }).click();
  await expect(alice.getByRole('heading', { name: loan, level: 1 })).toBeVisible();
  await alice.getByRole('link', { name: home }).click();

  await expect(alice.getByRole('heading', { name: home, level: 1 })).toBeVisible();
  const figure = (term: string) => alice.getByRole('region', { name: 'Aujourd’hui' }).locator(`dt:text-is("${term}") + dd`);
  await expect(figure('Prix de revient')).toContainText('281 000,00 €');
  await expect(figure('Valeur nette')).toContainText('90 000,00 €');
  await expect(alice.getByRole('region', { name: 'Crédits liés' }).getByRole('link', { name: loan })).toBeVisible();
  await expect(alice.getByRole('list', { name: 'Avertissements' })).toContainText(`Le crédit privé ${loan} ne sera pas déduit du patrimoine du foyer`);

  const bob = await browser.newPage();
  await signIn(bob, 'bob', '/properties');
  await expect(bob.getByRole('link', { name: home })).toBeVisible();

  const flat = await createProperty(alice, `Studio ${stamp}`, 'Non, privé');
  await bob.goto(flat);
  await expect(bob.getByText('Cet élément est introuvable.')).toBeVisible();
});

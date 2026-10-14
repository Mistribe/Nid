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

/**
 * Twelve rent dates inside the server's 12 rolling months, (today − 12 months, today], whatever its timezone: 15 days
 * before today, then a month earlier each time, the day capped at 28 so no month overflows.
 */
function rentDates(): string[] {
  const base = new Date(Date.now() - 15 * 86_400_000);
  return Array.from({ length: 12 }, (_, k) =>
    new Date(Date.UTC(base.getUTCFullYear(), base.getUTCMonth() - k, Math.min(base.getUTCDate(), 28))).toISOString().slice(0, 10));
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

test('alice lets her property: a tenancy under way, a yearly property tax, and its gross yield over twelve rents', async ({ page }) => {
  const stamp = Date.now();
  await signIn(page, 'alice', '/properties');
  await createProperty(page, `Locatif ${stamp}`, 'Oui, partagé');
  const id = new URL(page.url()).pathname.split('/').pop()!;
  const session = (await (await page.request.get('/auth/session')).json()) as { csrf: string };
  const record = async (data: object) => {
    const response = await page.request.post(`/api/v1/realestate/properties/${id}/flows`, { headers: { 'X-Nid-Csrf': session.csrf }, data });
    expect(response.status()).toBe(201);
  };
  // Its works, through the API to keep the journey short: the cost price is 281 000 €, as in the first journey.
  await record({ occurredOn: '2020-07-01', kind: 'expense', category: 'works', amount: '12000' });

  const rental = page.getByRole('region', { name: 'Location', exact: true });
  await expect(rental).toContainText('Ce bien n’est pas loué.');
  await rental.getByRole('button', { name: 'Ajouter un bail' }).click();
  const lease = rental.getByRole('form', { name: 'Nouveau bail' });
  await lease.getByLabel('Libellé', { exact: true }).fill('T2 meublé');
  await lease.getByLabel('Début', { exact: true }).fill('2021-01-01');
  await lease.getByLabel('Loyer mensuel', { exact: true }).fill('900');
  await lease.getByRole('button', { name: 'Ajouter' }).click();
  await expect(rental.getByRole('status')).toHaveText('Bail ajouté.');
  await expect(rental.getByRole('table', { name: 'Baux du bien' }).getByRole('row').nth(1)).toContainText('En cours');

  const costs = page.getByRole('region', { name: 'Charges prévues', exact: true });
  await costs.getByRole('button', { name: 'Ajouter une charge prévue' }).click();
  const tax = costs.getByRole('form', { name: 'Nouvelle charge prévue' });
  await tax.getByLabel('Catégorie', { exact: true }).selectOption('property_tax');
  await tax.getByLabel('Montant', { exact: true }).fill('1200');
  await tax.getByLabel('Tous les', { exact: true }).fill('1');
  await tax.getByLabel('Unité', { exact: true }).selectOption('year');
  await tax.getByLabel('Début', { exact: true }).fill('2021-10-15');
  await tax.getByRole('button', { name: 'Ajouter' }).click();
  await expect(costs.getByRole('status')).toHaveText('Charge prévue ajoutée.');
  await expect(costs.getByRole('table', { name: 'Charges prévues du bien' }).getByRole('row').nth(1)).toContainText('100,00 €');

  // Twelve rents of 900 € through the API, all within the last 12 months: 10 800 € over 281 000 € is 3,84 %.
  for (const occurredOn of rentDates()) await record({ occurredOn, kind: 'income', category: 'rent', amount: '900' });
  await page.reload();
  const figure = (term: string) => page.getByRole('region', { name: 'Location', exact: true }).locator(`dt:text-is("${term}") + dd`);
  await expect(figure('Loyers perçus sur 12 mois')).toContainText('10 800,00 €');
  await expect(figure('Rendement brut')).toContainText('3,84 %');
});

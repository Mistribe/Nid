import { readFile } from 'node:fs/promises';
import { expect, test } from '@playwright/test';
import { fillDate, pick, toasts } from './controls';
import { signIn } from './session';

test('alice reads her tax overview, follows a piste, opens Calcul, simulates, exports her 2042 and reaches the 2044 from her property', async ({ page }) => {
  const stamp = Date.now();
  const thisYear = new Date().getFullYear();
  const lastYear = thisYear - 1;
  await signIn(page, 'alice', '/tax');

  // A salary and a rented property with rent in the year just ended, so the 2042 has a box and the 2044 a property.
  const session = (await (await page.request.get('/auth/session')).json()) as { csrf: string; person: { id: string } };
  const headers = { 'X-Nid-Csrf': session.csrf };
  const salary = await page.request.post('/api/v1/income/sources', {
    headers, data: {
      displayName: `Salaire ${stamp}`, kind: 'salary', currency: 'EUR', visibility: 'shared', owners: [{ personId: session.person.id, shareBp: 10000 }],
      phases: [{ label: 'Salaire de base', startsOn: `${lastYear}-01-01`, netAmount: { amount: '3000', currency: 'EUR' }, every: { count: 1, unit: 'month' } }],
    },
  });
  expect(salary.status()).toBe(201);

  const home = `Studio ${stamp}`;
  await page.goto('/properties/new');
  await page.getByLabel('Nom', { exact: true }).fill(home);
  await pick(page, 'Type de bien', 'Bien locatif');
  await page.getByRole('radio', { name: 'Oui, partagé' }).click();
  await page.getByRole('button', { name: 'Continuer' }).click();
  await fillDate(page, 'Date d’achat', '2020-06-15');
  await page.getByLabel('Prix d’achat').fill('250000');
  await page.getByLabel('Frais de notaire').fill('19000');
  await page.getByRole('button', { name: 'Créer le bien' }).click();
  await expect(page.getByRole('heading', { name: home, level: 1 })).toBeVisible();
  const propertyUrl = page.url();
  const propertyId = new URL(propertyUrl).pathname.split('/').pop();
  for (const occurredOn of [`${lastYear}-03-15`, `${lastYear}-09-15`, `${thisYear}-01-01`]) {
    const flow = await page.request.post(`/api/v1/realestate/properties/${propertyId}/flows`, { headers, data: { occurredOn, kind: 'income', category: 'rent', amount: '900' } });
    expect(flow.status()).toBe(201);
  }

  // The overview: the hero, the split, then a piste leads into another tab.
  await page.goto('/tax');
  const overviewTab = page.getByRole('tab', { name: 'Vue d’ensemble' });
  await expect(overviewTab).toHaveAttribute('aria-selected', 'true');
  const hero = page.getByRole('region', { name: /^Impôt total \d{4} · estimation$/ });
  await expect(hero).toBeVisible();
  await expect(hero.getByRole('term')).toHaveCount(6);
  await expect(page.getByRole('region', { name: 'Où vont vos revenus' })).toBeVisible();
  await expect(page).toHaveURL(/\/tax$/);

  // The shared salary above (open-ended, from last year) is in the household's estimate, so the PER piste (room left, tax saved) shows.
  const pistes = page.getByRole('region', { name: 'Pistes' });
  await expect(pistes, 'the PER piste (this year, room left and tax saved) should show').toBeVisible();
  await expect(pistes.getByRole('link', { name: /sur le PER avant le 31 décembre/ }).first()).toBeVisible();
  await pistes.getByRole('link').first().click();
  await expect(page).toHaveURL(/[?&]tab=(detail|profile|withholding)/);
  await expect(overviewTab).toHaveAttribute('aria-selected', 'false');

  // Calcul: the steps from income to tax, as a table on demand.
  await page.getByRole('tab', { name: 'Calcul' }).click();
  await expect(page).toHaveURL(/tab=detail/);
  const steps = page.getByRole('region', { name: 'Du revenu à l’impôt' });
  await expect(steps).toBeVisible();
  await steps.getByRole('button', { name: 'Afficher en tableau' }).click();
  await expect(steps.getByRole('table')).toBeVisible();

  // Simuler: the before / after.
  await page.getByRole('tab', { name: 'Simuler' }).click();
  await page.getByRole('button', { name: 'Calculer' }).click();
  await expect(toasts(page)).toContainText('Simulation calculée.');
  await expect(page.getByRole('region', { name: 'Avant / après' }).locator('[data-stat-line]').first()).toContainText('€');

  // Déclarations: the 2042's CSV (the tab's default year is the year just ended, from the date alone, so 1 January needs nothing more).
  await page.getByRole('tab', { name: 'Déclarations' }).click();
  await expect(page.getByRole('radio', { name: '2042' })).toBeChecked();
  const download = page.waitForEvent('download');
  await page.getByRole('button', { name: 'Exporter en CSV' }).click();
  const file = await download;
  expect(file.suggestedFilename()).toBe(`declaration-2042-${lastYear}.csv`);
  expect(await readFile(await file.path(), 'utf-8')).toContain('1AJ');

  // From the property to the 2044.
  await page.goto(propertyUrl);
  await page.getByRole('link', { name: 'Déclaration 2044' }).click();
  await expect(page).toHaveURL(/\/tax\?tab=declarations&form=2044$/);
  await expect(page.getByRole('tab', { name: 'Déclarations' })).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByRole('radio', { name: '2044' })).toBeChecked();
  await expect(page.getByRole('region', { name: 'Totaux du foyer' })).toBeVisible();
});

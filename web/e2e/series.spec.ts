import { expect, test, type Page } from '@playwright/test';
import { signIn } from './session';

/**
 * The CI stack holds no CPI, IRL or exchange rate, and the manual writes refuse to overwrite INSEE's values: read the catalogue
 * and write, as alice (admin), only the series it lacks, with invented round values in last year (inside « 5 ans »).
 */
async function ensureSeries(page: Page): Promise<void> {
  const { csrf } = (await (await page.request.get('/auth/session')).json()) as { csrf: string };
  const y = new Date().getFullYear() - 1;
  const has = async (key: string) => {
    const response = await page.request.get('/api/v1/series', { params: { q: key } });
    expect(response.ok()).toBe(true);
    return ((await response.json()) as { items: { key: string }[] }).items.some((i) => i.key === key);
  };
  const put = async (url: string, data: object) => {
    const response = await page.request.put(url, { headers: { 'X-Nid-Csrf': csrf }, data });
    expect(response.ok()).toBe(true);
  };
  if (!(await has('cpi'))) {
    for (const [month, value] of [[1, '100'], [2, '100.5'], [3, '101']] as const) await put(`/api/v1/core/inflation/${y}/${month}`, { value });
  }
  if (!(await has('irl'))) {
    await put(`/api/v1/realestate/irl/${y}/1`, { value: '130', publishedOn: `${y}-04-15` });
    await put(`/api/v1/realestate/irl/${y}/2`, { value: '131', publishedOn: `${y}-07-15` });
  }
  if (!(await has('fx:EUR'))) await put(`/api/v1/core/fx-rates/${y}-03-02/EUR`, { rate: '0.9' });
}

test('alice opens the CPI from Administration › Inflation, adds the IRL then a rate, and reads them in base 100', async ({ page }) => {
  await signIn(page, 'alice', '/admin/inflation');
  await ensureSeries(page);
  await page.reload();
  await page.getByRole('link', { name: 'Voir la série' }).click();
  await expect.poll(() => new URL(page.url()).searchParams.get('s')).toBe('cpi');

  await page.getByRole('combobox', { name: 'Ajouter une série' }).click();
  await page.getByPlaceholder('Rechercher une série…').fill('irl'); // found by its key on the server
  await page.getByRole('option', { name: 'Indice de référence des loyers (IRL)', exact: true }).click();
  await expect.poll(() => new URL(page.url()).searchParams.get('s')).toBe('cpi,irl');
  await expect(page.getByRole('button', { name: 'Retirer Indice de référence des loyers (IRL)' })).toBeVisible(); // the points are in
  await expect(page.getByRole('radiogroup', { name: 'Échelle' })).toHaveCount(0); // two indices share one axis

  await page.getByRole('combobox', { name: 'Ajouter une série' }).click();
  await page.getByPlaceholder('Rechercher une série…').fill('eur'); // the stack may hold every currency: past the first page
  await page.getByRole('option', { name: 'EUR pour 1 USD', exact: true }).click();
  await expect(page.getByText(/^Base 100 le /)).toBeVisible({ timeout: 15_000 }); // years of daily rates: a slow runner draws them past 5 s
  await expect(page.getByRole('radio', { name: 'Base 100' })).toBeChecked();
  await page.goBack();
  await expect(page).toHaveURL(/\/admin\/inflation$/); // chip edits replaced the entry
});

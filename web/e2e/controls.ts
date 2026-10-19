import type { Locator, Page } from '@playwright/test';

/** Opens the combobox labelled `label` and clicks the option. */
export async function pick(page: Page | Locator, label: string | RegExp, option: string | RegExp): Promise<void> {
  await page.getByRole('combobox', { name: label }).click();
  await page.getByRole('option', { name: option, exact: typeof option === 'string' }).click();
}

/** Types an ISO date (yyyy-mm-dd) as jj/mm/aaaa into the date field labelled `label`. */
export async function fillDate(page: Page | Locator, label: string | RegExp, iso: string): Promise<void> {
  const [y, m, d] = iso.split('-');
  await page.getByLabel(label, { exact: true }).fill(`${d}/${m}/${y}`);
}

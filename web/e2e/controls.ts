import type { Locator, Page } from '@playwright/test';

/**
 * Opens the combobox labelled `label` (inside `scope`) and clicks the option. The options are looked for in the whole page: a
 * searchable combobox opens its list in a popover of its own, outside the dialog that holds the field.
 */
export async function pick(scope: Page | Locator, label: string | RegExp, option: string | RegExp): Promise<void> {
  await scope.getByRole('combobox', { name: label }).click();
  const page = 'goto' in scope ? scope : scope.page();
  await page.getByRole('option', { name: option, exact: typeof option === 'string' }).click();
}

/** Types an ISO date (yyyy-mm-dd) as jj/mm/aaaa into the date field labelled `label`. */
export async function fillDate(page: Page | Locator, label: string | RegExp, iso: string): Promise<void> {
  const [y, m, d] = iso.split('-');
  await page.getByLabel(label, { exact: true }).fill(`${d}/${m}/${y}`);
}

/** The live region of the shell's toasts: what an action announced (design-system.md §5). */
export function toasts(page: Page): Locator {
  return page.getByRole('region', { name: /^Messages/ });
}

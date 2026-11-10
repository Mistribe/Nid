import { expect, test } from '@playwright/test';
import { pick } from './controls';
import { signIn } from './session';

// The e2e tsconfig has no DOM lib: the page code goes in as a string, as in the other specs.
const BACKGROUND = `getComputedStyle(document.body).backgroundColor`;

test('alice reviews her year and prints it on A4, light even on a dark screen', async ({ browser }) => {
  const name = `Livret bilan ${Date.now()}`;
  const alice = await browser.newPage();
  await signIn(alice, 'alice', '/investments/new');
  await alice.getByLabel('Nom').fill(name);
  await pick(alice, 'Type', 'Livret');
  await alice.getByRole('radio', { name: /Partagé/ }).check();
  await alice.getByRole('button', { name: 'Première valeur' }).click();
  await alice.getByLabel('Montant de la première valeur').fill('1000');
  await alice.getByRole('button', { name: 'Créer l’enveloppe' }).click();
  await expect(alice.getByRole('heading', { name, level: 1 })).toBeVisible();

  await alice.goto('/reports/year');
  await expect(alice.getByRole('heading', { name: /^Bilan \d{4}/, level: 1 })).toBeVisible();
  await expect(alice.getByRole('region', { name: 'Patrimoine net' })).toContainText('€');
  const printButton = alice.getByRole('button', { name: 'Imprimer' });
  await expect(printButton).toBeVisible();
  await expect(alice.getByRole('combobox', { name: 'Année du bilan' })).toBeVisible();

  // The app's dark mode is the class on <html> (app/theme.ts); set here, not in alice's preferences, which other journeys share.
  await alice.emulateMedia({ colorScheme: 'dark' });
  await alice.evaluate(`document.documentElement.classList.add('dark')`);
  const dark = await alice.evaluate<string>(BACKGROUND);
  await alice.evaluate(`document.documentElement.classList.remove('dark')`);
  const light = await alice.evaluate<string>(BACKGROUND);
  await alice.evaluate(`document.documentElement.classList.add('dark')`);
  expect(dark, 'the dark theme changes the background on screen').not.toBe(light);

  // The A4 printable width at 96 dpi: 210 mm less two 15 mm margins.
  await alice.setViewportSize({ width: 680, height: 960 });
  await alice.emulateMedia({ media: 'print', colorScheme: 'dark' });
  expect(await alice.evaluate<boolean>(`document.documentElement.classList.contains('dark')`)).toBe(true);
  await expect.poll(() => alice.evaluate<string>(BACKGROUND)).toBe(light);
  // Nothing wider than the page, which would be cut on paper: the header, the sections this journey fills and each table in them
  // (whose container no longer scrolls). Only those: alice's other sections hold what other journeys left on her shared account.
  // One pixel of slack: both widths are rounded integers, so sub-pixel rounding alone could tip one over.
  const fits = `((boxes) => boxes.some((b) => b.matches('[data-slot=table-container]')) && boxes.every((b) => b.scrollWidth <= b.clientWidth + 1))(
    [...document.querySelectorAll('section')].filter((s) => ['Patrimoine net', 'Placements'].includes(s.querySelector('h2')?.textContent ?? ''))
      .flatMap((s) => [s, ...s.querySelectorAll('[data-slot=table-container]')]).concat([...document.querySelectorAll('[data-page-header]')]))`;
  await expect.poll(() => alice.evaluate<boolean>(fits)).toBe(true);
  // The sidebar is hidden at this width anyway; the top bar and the phone tabs are not, on screen.
  await expect(alice.locator('[data-pane="toolbar"]')).toBeHidden();
  await expect(alice.locator('[data-pane="tabs"]')).toBeHidden();
  await expect(alice.getByRole('combobox', { name: 'Année du bilan' })).toBeHidden();
  await expect(printButton).toBeHidden();
  await expect(alice.getByRole('region', { name: 'Patrimoine net' })).toBeVisible();
  // At the page's width a list replaces a table on screen; paper keeps the table and its columns.
  await expect(alice.getByRole('region', { name: 'Placements' }).getByRole('table').first()).toBeVisible();

  const pdf = await alice.pdf({ format: 'A4' });
  expect(pdf.byteLength).toBeGreaterThan(0);
});

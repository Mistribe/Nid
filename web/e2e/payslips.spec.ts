import { expect, test } from '@playwright/test';
import { fillDate } from './controls';
import { aMonthAgo, plusMonths } from './dates';
import { signIn } from './session';

// The name in a comment line: the same bytes twice are one document (duplicate check), across journeys too.
const pdf = (name: string) => ({ name, mimeType: 'application/pdf', buffer: Buffer.from(`%PDF-1.4\n% ${name}\n1 0 obj<<>>endobj\ntrailer<<>>\n%%EOF\n`) });
/** « septembre 2026 », as the history's month column and the payslip title write it. */
const label = (iso: string) => new Intl.DateTimeFormat('fr-FR', { timeZone: 'UTC', month: 'long', year: 'numeric' }).format(new Date(iso));

test('alice attaches, removes and drops payslips on a salary history', async ({ browser }) => {
  const name = `Salaire ${Date.now()}`;
  const lastMonth = aMonthAgo();
  const start = plusMonths(lastMonth, -5); // six months ago: five months before last month
  const [m1, m2, m3] = [lastMonth, plusMonths(lastMonth, -1), plusMonths(lastMonth, -2)] as [string, string, string];
  const alice = await browser.newPage();
  await signIn(alice, 'alice', '/income/new');

  await alice.getByLabel('Nom', { exact: true }).fill(name);
  await alice.getByRole('combobox', { name: 'Type' }).click();
  await alice.getByRole('option', { name: 'Salaire' }).click();
  await alice.getByRole('radio', { name: 'Non, privé' }).click();
  await alice.getByRole('button', { name: 'Continuer' }).click();
  const base = alice.getByRole('group', { name: 'Composante 1' });
  await base.getByLabel('Montant net').fill('2300');
  await fillDate(base, 'Début', start);
  await alice.getByRole('button', { name: 'Créer le revenu' }).click();
  await expect(alice.getByRole('heading', { name, level: 1 })).toBeVisible();

  await alice.getByRole('tab', { name: 'Historique' }).click();
  const table = alice.getByRole('table', { name: 'Versements, mois par mois' });
  // A month is one row group (its header cell spans the rows): a row filter would match the month's header row and its payment row.
  const row = (iso: string) => table.getByRole('rowgroup').filter({ has: alice.locator('th[scope="rowgroup"]', { hasText: label(iso) }) });

  // The second month back is attached first: the missing rule starts at the oldest payslip.
  await row(m2).getByLabel('Joindre').setInputFiles(pdf('un.pdf'));
  await expect(row(m2).getByRole('link', { name: `Bulletin de paie ${label(m2)}` })).toBeVisible();

  await row(m1).getByLabel('Joindre').setInputFiles(pdf('deux.pdf'));
  const attached = row(m1).getByRole('link', { name: `Bulletin de paie ${label(m1)}` });
  await expect(attached).toBeVisible();

  await row(m1).getByRole('button', { name: /^Actions du bulletin/ }).click();
  await alice.getByRole('menuitem', { name: 'Retirer' }).click();
  await alice.getByRole('alertdialog').getByRole('button', { name: 'Confirmer' }).click();
  await expect(row(m1)).toContainText('Pas de bulletin');
  await expect(attached).toHaveCount(0);

  // Two files named with their month: last month again, and the third month back.
  await alice.getByRole('button', { name: 'Déposer des bulletins' }).click();
  const dialog = alice.getByRole('dialog', { name: 'Déposer des bulletins' });
  await dialog.getByLabel('Déposer', { exact: true }).setInputFiles([pdf(`bulletin-${m1.slice(0, 7)}.pdf`), pdf(`bulletin-${m3.slice(0, 7)}.pdf`)]);
  await expect(dialog.getByRole('combobox', { name: `Mois de bulletin-${m1.slice(0, 7)}.pdf` })).toContainText(label(m1));
  await dialog.getByRole('button', { name: 'Envoyer' }).click();
  await expect(dialog.getByText('Envoyé')).toHaveCount(2);
  await dialog.getByRole('button', { name: 'Fermer', exact: true }).first().click();

  for (const iso of [m3, m2, m1]) {
    await expect(row(iso).getByRole('link', { name: `Bulletin de paie ${label(iso)}` })).toBeVisible();
    await expect(row(iso)).not.toContainText('Pas de bulletin');
  }
});

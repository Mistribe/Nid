import { expect, test } from '@playwright/test';
import { fillDate } from './controls';
import { signIn } from './session';

test('alice sets her departure and a living cost, then enters her pension on the Retraite page', async ({ browser }) => {
  const alice = await browser.newPage();
  await signIn(alice, 'alice', '/settings/projection');

  // Re-runnable: « Saisir la pension » only shows while Alice has no pension source, so remove those a previous run left.
  const { csrf } = (await (await alice.request.get('/auth/session')).json()) as { csrf: string };
  const headers = { 'X-Nid-Csrf': csrf };
  const sources = (await (await alice.request.get('/api/v1/income/sources?scope=me')).json()) as { items: { id: string; kind: string; canWrite: boolean }[] };
  for (const source of sources.items.filter((i) => i.kind === 'pension' && i.canWrite)) {
    const { version } = (await (await alice.request.get(`/api/v1/income/sources/${source.id}/deletion`)).json()) as { version: number };
    expect((await alice.request.delete(`/api/v1/income/sources/${source.id}?version=${version}`, { headers })).ok()).toBe(true);
  }

  await alice.getByRole('button', { name: /Modifier la retraite de Alice/ }).click();
  const departure = alice.getByRole('dialog');
  await fillDate(departure, 'Date de naissance', '1970-03-15');
  await departure.getByLabel('Âge de départ à la retraite').fill('64');
  await departure.getByLabel('Mois').fill('6');
  await departure.getByRole('button', { name: 'Enregistrer' }).click();
  await expect(departure).toBeHidden();

  await alice.getByRole('button', { name: 'Modifier les hypothèses' }).click();
  const assumptions = alice.getByRole('dialog');
  await assumptions.getByLabel(/Coût de la vie mensuel/).fill('2500');
  await assumptions.getByRole('button', { name: 'Enregistrer' }).click();
  await expect(assumptions).toBeHidden();

  await alice.getByRole('link', { name: 'Retraite', exact: true }).first().click();
  await expect(alice.getByRole('heading', { name: 'Retraite', level: 1 })).toBeVisible();
  // 1970-03 + 64 years 6 months: September 2034.
  const row = alice.getByRole('row', { name: /Alice/ });
  await expect(row).toContainText('2034');

  await alice.getByRole('button', { name: /Actions pour Alice/ }).click();
  await alice.getByRole('menuitem', { name: 'Saisir la pension' }).click();
  const pension = alice.getByRole('dialog');
  await pension.getByLabel('Pension mensuelle nette').fill('1500');
  await expect(pension.getByLabel("Visible de l'autre adulte")).toBeChecked();
  await pension.getByRole('button', { name: 'Enregistrer' }).click();
  await expect(pension).toBeHidden();
  await expect(alice.getByRole('row', { name: /Alice/ })).toContainText(/1\s500,00\s€/);
  await expect(alice.getByRole('row', { name: /Alice/ }).getByRole('link', { name: 'Pension de retraite' })).toBeVisible();

  // With a living cost set, independence is either a month or "not reached": never the missing-cost warning.
  await expect(alice.getByText('Indépendance financière').first()).toBeVisible();
  await expect(alice.getByText(/^(\p{L}+\.? \d{4}|Non atteinte avant l'horizon)$/u).first()).toBeVisible();
  await expect(alice.getByText(/Renseignez le coût de la vie mensuel/)).toHaveCount(0);
});

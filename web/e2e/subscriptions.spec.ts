import { expect, test } from '@playwright/test';
import { fillDate } from './controls';
import { signIn } from './session';

test('alice adds a recurring subscription and sees its cost; bob cannot reach her private one', async ({ browser }) => {
  const stamp = Date.now().toString(36);
  const name = `E2E privé ${stamp}`;

  // Separate contexts: each adult has their own session cookie.
  const alice = await (await browser.newContext({ ignoreHTTPSErrors: true })).newPage();
  await signIn(alice, 'alice', '/subscriptions');
  await alice.getByRole('link', { name: 'Ajouter un abonnement' }).first().click();
  // The wizard loads lazily: wait for it, then match labels exactly ("Nom" is also in "Rechercher par nom").
  await expect(alice.getByRole('heading', { level: 1, name: 'Ajouter un abonnement' })).toBeVisible();
  await alice.getByRole('button', { name: 'Continuer' }).click(); // the type step: Récurrent is preselected
  await alice.getByLabel('Nom', { exact: true }).fill(name);
  await alice.getByRole('radio', { name: 'Privé', exact: true }).click();
  await alice.getByRole('button', { name: 'Continuer' }).click();
  await fillDate(alice, 'Début', '2025-01-01');
  await alice.getByLabel('Montant', { exact: true }).fill('7,49');
  await alice.getByRole('button', { name: 'Continuer' }).click();
  await alice.getByRole('button', { name: "Ajouter l'abonnement" }).click();

  await expect(alice.getByRole('heading', { level: 1, name })).toBeVisible();
  const detailUrl = new URL(alice.url()).pathname;
  await alice.goto('/subscriptions?scope=me&tab=list&group=none');
  await expect(alice.getByRole('table', { name: 'Liste des abonnements' }).getByRole('row').filter({ hasText: name })).toContainText('7,49');

  const bob = await (await browser.newContext({ ignoreHTTPSErrors: true })).newPage();
  await signIn(bob, 'bob', '/subscriptions');
  await bob.goto('/subscriptions?scope=me&tab=list&group=none');
  await expect(bob.getByRole('heading', { level: 1, name: 'Abonnements' })).toBeVisible();
  await expect(bob.getByText(name)).toHaveCount(0);
  await bob.goto(detailUrl);
  await expect(bob.getByRole('heading', { name: 'Page introuvable', level: 1 })).toBeVisible();
});

test('alice records a free trial followed by a monthly price', async ({ browser }) => {
  const alice = await browser.newPage();
  await signIn(alice, 'alice', '/subscriptions/new');
  const name = `Essai E2E ${Date.now().toString(36)}`;
  await alice.getByRole('radio', { name: /Essai ou offre de lancement/ }).click();
  await alice.getByRole('button', { name: 'Continuer' }).click();
  await alice.getByLabel('Nom', { exact: true }).fill(name);
  await alice.getByRole('button', { name: 'Continuer' }).click();
  await fillDate(alice, 'Début', '2026-01-01');
  await alice.getByRole('button', { name: 'Continuer' }).click();
  await expect(alice.getByRole('radio', { name: 'Un abonnement récurrent' })).toBeChecked();
  await alice.getByLabel('Montant', { exact: true }).fill('9,99');
  await alice.getByRole('button', { name: 'Continuer' }).click();
  await alice.getByRole('button', { name: "Ajouter l'abonnement" }).click();

  await expect(alice.getByRole('heading', { level: 1, name })).toBeVisible();
  await expect(alice.getByText('Essai', { exact: false }).first()).toBeVisible(); // the coverage band's legend
  // The free trial covers one month from 2026-01-01: the monthly price's first charge follows it, on 2026-02-01
  // (final review, finding 3; 1E2b1 design §5). "Prochaine échéance" moves on with today's date, so the covered
  // periods table (a historical record) is what shows this first charge, not that paragraph.
  await expect(alice.getByRole('row', { name: /^1 févr\. 2026.*Payé/ })).toBeVisible();
});

// 1E2b2: the label `other` comes with the migrations, so the journey needs no catalog sync and no other journey's data.
test('alice creates her own provider from the wizard, then finds the subscription by its label', async ({ page }) => {
  const stamp = Date.now().toString(36);
  const provider = `Fournisseur E2E ${stamp}`;
  const name = `E2E libellé ${stamp}`;

  await signIn(page, 'alice', '/subscriptions/new');
  await expect(page.getByRole('heading', { level: 1, name: 'Ajouter un abonnement' })).toBeVisible();
  await page.getByRole('button', { name: 'Continuer' }).click();
  await page.getByLabel('Nom', { exact: true }).fill(name);
  await page.getByRole('button', { name: 'Nouveau fournisseur' }).click();
  const dialog = page.getByRole('dialog', { name: 'Nouveau fournisseur' });
  await dialog.getByLabel('Nom', { exact: true }).fill(provider);
  await dialog.getByRole('checkbox', { name: 'Other' }).click();
  await dialog.getByRole('button', { name: 'Créer' }).click();
  await expect(dialog).toBeHidden();
  await expect(page.getByRole('combobox', { name: 'Fournisseur' })).toContainText(provider);
  await page.getByRole('button', { name: 'Continuer' }).click();
  await fillDate(page, 'Début', '2025-01-01');
  await page.getByLabel('Montant', { exact: true }).fill('4,99');
  await page.getByRole('button', { name: 'Continuer' }).click();
  await page.getByRole('button', { name: "Ajouter l'abonnement" }).click();
  await expect(page.getByRole('heading', { level: 1, name })).toBeVisible();

  await page.goto('/subscriptions?tab=list&group=none');
  await page.getByRole('button', { name: 'Libellés' }).click();
  await page.getByRole('menuitemcheckbox', { name: 'Other' }).click();
  await page.keyboard.press('Escape');
  await expect(page).toHaveURL(/[?&]label=other(?:&|$)/);
  await expect(page.getByRole('link', { name: new RegExp(name) })).toBeVisible();
});

test('alice opens the calendar from the overview and reaches a subscription from a charge', async ({ browser }) => {
  const alice = await browser.newPage();
  const name = `Calendrier E2E ${Date.now().toString(36)}`;
  await signIn(alice, 'alice', '/subscriptions/new');
  await alice.getByRole('button', { name: 'Continuer' }).click(); // the type step: Récurrent is preselected
  await alice.getByLabel('Nom', { exact: true }).fill(name);
  await alice.getByRole('button', { name: 'Continuer' }).click();
  await fillDate(alice, 'Début', '2025-01-01');
  await alice.getByLabel('Montant', { exact: true }).fill('4,32');
  await alice.getByRole('button', { name: 'Continuer' }).click();
  await alice.getByRole('button', { name: "Ajouter l'abonnement" }).click();
  await expect(alice.getByRole('heading', { level: 1, name })).toBeVisible();

  await alice.goto('/subscriptions');
  await alice.getByRole('region', { name: 'Prochains prélèvements' }).getByRole('link', { name: 'Calendrier', exact: true }).click();
  await expect(alice).toHaveURL(/tab=calendar/);
  // Monthly since 2025-01-01: charged on the 1st of the current month. The shared database holds many charges on the
  // 1st, so the day cell may only show a few and fold the rest into « +N »: open that popover when the link is not there.
  const cell = alice.getByRole('group', { name: 'Calendrier du mois' }).getByRole('group', { name: /^[^\d]+ 1(er)? [^\d]+$/ }).first();
  await expect(cell).toBeVisible(); // the grid renders once the month has loaded
  const charge = cell.getByRole('link', { name });
  if (!(await charge.isVisible())) {
    await cell.getByRole('button', { name: /autres? prélèvements?$/ }).click();
    await alice.getByRole('dialog').getByRole('link', { name }).click();
  } else await charge.click();
  await expect(alice.getByRole('heading', { level: 1, name })).toBeVisible();
});

import { expect, test } from '@playwright/test';
import { fillDate } from './controls';
import { signIn } from './session';

/**
 * The centre end to end (notifications design §11): the evaluation runs on the server, so the journey only creates
 * data and asks for the count again — `GET /count` requests a refresh, throttled by `Notifications:RefreshThrottleSeconds`
 * (5 s on the dev and smoke stacks, 900 s in production).
 */
test('alice sees a lease ending in 80 days, snoozes it and finds it under Reportées', async ({ page }) => {
  const name = `Echéance ${Date.now()}`;
  await signIn(page, 'alice', '/properties/new');
  await page.getByLabel('Nom', { exact: true }).fill(name);
  await page.getByRole('radio', { name: 'Oui, partagé' }).click();
  await page.getByRole('button', { name: 'Continuer' }).click();
  await fillDate(page, 'Date d’achat', '2020-06-15');
  await page.getByLabel('Prix d’achat').fill('250000');
  await page.getByRole('button', { name: 'Créer le bien' }).click();
  await expect(page.getByRole('heading', { name, level: 1 })).toBeVisible();

  // A lease ending in 80 days, through the API to keep the journey short.
  const id = new URL(page.url()).pathname.split('/').pop()!;
  const session = (await (await page.request.get('/auth/session')).json()) as { csrf: string };
  const endsOn = new Date(Date.now() + 80 * 86_400_000).toISOString().slice(0, 10);
  const created = await page.request.post(`/api/v1/realestate/properties/${id}/tenancies`, {
    headers: { 'X-Nid-Csrf': session.csrf },
    data: { label: 'T2', startsOn: '2021-01-01', endsOn, rentMonthly: '900' },
  });
  expect(created.status()).toBe(201);
  // The first count (at app load) came before the data existed and the throttle drops the next ones for a few seconds:
  // open the page again until the evaluation has run and the row is there. Other journeys leave notifications of alice's
  // behind, so the row is found by its property, not by the bell's number.
  const todo = page.getByRole('tabpanel', { name: 'À traiter' });
  // The lease since 2021 has no rent either: the property also raises « Des loyers sont impayés », so pick the row by its title.
  const row = todo.getByRole('row').filter({ hasText: name }).filter({ hasText: 'Un bail arrive à échéance' });
  await expect(async () => {
    await page.goto('/notifications');
    await expect(row).toHaveCount(1, { timeout: 3_000 });
  }).toPass({ timeout: 30_000, intervals: [2_000, 3_000, 5_000] });
  await expect(page.getByRole('button', { name: /^Notifications, \d+ non lues?$/ })).toBeVisible();

  await row.getByRole('button', { name: 'Actions' }).click();
  await page.getByRole('menuitem', { name: 'Reporter' }).click();
  // Keyboard, as the component test does: a pointer move off the submenu's trigger closes it before the click lands.
  await page.getByRole('menuitem', { name: '1 semaine' }).focus();
  await page.keyboard.press('Enter');
  await expect(row).toHaveCount(0);

  await page.getByRole('tab', { name: 'Reportées' }).click();
  await expect(page).toHaveURL(/tab=snoozed/);
  await expect(page.getByRole('row').filter({ hasText: name }).filter({ hasText: 'Un bail arrive à échéance' })).toHaveCount(1);
});

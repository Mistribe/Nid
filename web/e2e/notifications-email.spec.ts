import { expect, test, type APIRequestContext } from '@playwright/test';
import { fillDate } from './controls';
import { signIn } from './session';

/**
 * Email delivery end to end (notifications design §5): the dev and smoke stacks send to a Mailpit catcher, whose API this
 * journey reads (`MAILPIT_URL`, default http://localhost:8025). The dispatcher runs every minute, so the lease email is
 * polled for up to two and a half minutes. Alice's address is alice@example.test (deploy/local/bootstrap-pocket-id.mjs).
 */
const MAILPIT = process.env.MAILPIT_URL ?? 'http://localhost:8025';
const ALICE = 'alice@example.test';

type Summary = { ID: string; Subject: string; To: Array<{ Address: string }> };

async function messagesTo(request: APIRequestContext, address: string, subject: string): Promise<Summary[]> {
  const response = await request.get(`${MAILPIT}/api/v1/messages`);
  expect(response.ok()).toBe(true);
  const { messages } = (await response.json()) as { messages: Summary[] };
  return messages.filter((m) => m.Subject === subject && m.To.some((to) => to.Address === address));
}

test('alice turns email on, gets a test email and the lease reminder', async ({ page, request }) => {
  test.setTimeout(240_000);
  // Earlier runs leave messages: start from an empty catcher.
  expect((await request.delete(`${MAILPIT}/api/v1/messages`)).ok()).toBe(true);

  await signIn(page, 'alice', '/settings/notifications');
  const email = page.getByRole('checkbox', { name: 'Email' });
  await expect(email).toBeEnabled();
  if (!(await email.isChecked())) await email.click();
  await page.getByRole('button', { name: 'Enregistrer' }).click();
  await expect(page.getByText('Réglages enregistrés.')).toBeVisible();

  await page.getByRole('button', { name: 'Envoyer un test' }).click();
  await expect(page.getByText('Email envoyé.')).toBeVisible();
  await expect(async () => {
    expect(await messagesTo(request, ALICE, 'Nid : message de test')).toHaveLength(1);
  }).toPass({ timeout: 30_000, intervals: [1_000, 2_000] });

  // A property with a lease ending in 80 days, as in notifications.spec.ts.
  const name = `Courrier ${Date.now()}`;
  await page.goto('/properties/new');
  await page.getByLabel('Nom', { exact: true }).fill(name);
  await page.getByRole('radio', { name: 'Oui, partagé' }).click();
  await page.getByRole('button', { name: 'Continuer' }).click();
  await fillDate(page, 'Date d’achat', '2020-06-15');
  await page.getByLabel('Prix d’achat').fill('250000');
  await page.getByRole('button', { name: 'Créer le bien' }).click();
  await expect(page.getByRole('heading', { name, level: 1 })).toBeVisible();
  const id = new URL(page.url()).pathname.split('/').pop()!;
  const session = (await (await page.request.get('/auth/session')).json()) as { csrf: string };
  const endsOn = new Date(Date.now() + 80 * 86_400_000).toISOString().slice(0, 10);
  const created = await page.request.post(`/api/v1/realestate/properties/${id}/tenancies`, {
    headers: { 'X-Nid-Csrf': session.csrf },
    data: { label: 'T2', startsOn: '2021-01-01', endsOn, rentMonthly: '900' },
  });
  expect(created.status()).toBe(201);

  // Opening the centre requests the evaluation (throttled to 5 s here); the dispatcher then sends within the minute.
  const lease = page.getByRole('tabpanel', { name: 'À traiter' }).getByRole('row').filter({ hasText: name }).filter({ hasText: 'Un bail arrive à échéance' });
  await expect(async () => {
    await page.goto('/notifications');
    await expect(lease).toHaveCount(1, { timeout: 3_000 });
  }).toPass({ timeout: 30_000, intervals: [2_000, 3_000, 5_000] });

  let received: Summary | undefined;
  await expect(async () => {
    // The subject carries no item name: the body does.
    for (const m of await messagesTo(request, ALICE, 'Nid : Un bail arrive à échéance')) {
      const body = (await (await request.get(`${MAILPIT}/api/v1/message/${m.ID}`)).json()) as { Text: string };
      if (body.Text.includes(name)) received = m;
    }
    expect(received).toBeDefined();
  }).toPass({ timeout: 150_000, intervals: [5_000, 10_000] });
});

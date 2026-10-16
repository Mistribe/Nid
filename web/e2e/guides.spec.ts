import { expect, test } from '@playwright/test';
import { signIn } from './session';

// The server's map is never changed: the session is served with nothing seen, and the preferences call is answered locally.
test('an unseen guide plays once, « Passer » records it and « Guide de cette page » replays it', async ({ browser }) => {
  const alice = await browser.newPage();
  await signIn(alice, 'alice', '/dashboard');

  const sent: Array<{ guidesSeen: Record<string, number> }> = [];
  await alice.route('**/auth/session', async (route) => {
    const body = (await (await route.fetch()).json()) as { preferences: { guidesSeen: Record<string, number> } };
    body.preferences.guidesSeen = {};
    await route.fulfill({ json: body });
  });
  await alice.route('**/api/v1/core/me/preferences', async (route) => {
    const body = route.request().postDataJSON() as { guidesSeen: Record<string, number> };
    sent.push(body);
    await route.fulfill({ json: body });
  });
  await alice.reload();

  const intro = alice.getByRole('dialog', { name: 'Accueil : premiers pas' });
  await expect(intro).toBeVisible();
  await intro.getByRole('button', { name: 'Passer' }).click();
  await expect(intro).toBeHidden();
  expect(sent.at(-1)?.guidesSeen.dashboard).toBe(1);

  await alice.getByRole('button', { name: 'Guide de cette page' }).click();
  await expect(alice.getByRole('dialog', { name: 'Accueil : premiers pas' })).toBeVisible();
});

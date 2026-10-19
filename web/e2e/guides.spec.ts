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
  expect(sent.at(-1)?.guidesSeen.dashboard).toBe(2);

  await alice.getByRole('button', { name: 'Guide de cette page' }).click();
  await expect(alice.getByRole('dialog', { name: 'Accueil : premiers pas' })).toBeVisible();
});

// Spec §4.4: the card covers the bottom of a phone's screen, so each target is scrolled to the top, clear of it.
test('on a 375 px phone each step of the dashboard tour shows its target above the card', async ({ browser }) => {
  const alice = await browser.newPage({ viewport: { width: 375, height: 667 }, reducedMotion: 'reduce' });
  await signIn(alice, 'alice', '/dashboard');

  await alice.getByRole('button', { name: 'Guide de cette page' }).click();
  const intro = alice.getByRole('dialog', { name: 'Accueil : premiers pas' });
  for (let i = 1; i < 4; i++) await intro.getByRole('button', { name: 'Suivant' }).click();
  await intro.getByRole('button', { name: 'Me montrer à l’écran' }).click();

  const card = alice.getByRole('dialog');
  const target = alice.locator('[data-guide-active]');
  for (;;) {
    await expect(async () => {
      const t = (await target.boundingBox())!;
      const c = (await card.boundingBox())!;
      expect(t.y).toBeGreaterThanOrEqual(0);
      // A target taller than the room above the card shows its start, its heading at least.
      expect(t.y + Math.min(t.height, 48)).toBeLessThanOrEqual(c.y);
    }).toPass();
    const next = card.getByRole('button', { name: 'Suivant' });
    if (!(await next.isVisible())) break;
    await next.click();
  }
  await card.getByRole('button', { name: 'Terminer' }).click();
  await expect(card).toBeHidden();
});

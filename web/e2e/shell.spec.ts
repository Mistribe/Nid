import { expect, test } from '@playwright/test';
import { signIn } from './session';

// 2E1: on a phone, every tab — « Plus » included — fits on screen; jsdom cannot see a layout, so this runs in a browser.
for (const width of [320, 375, 414]) {
  test(`the bottom tabs fit a ${width}px phone and « Plus » leads to the other sections`, async ({ browser }) => {
    const alice = await browser.newPage({ viewport: { width, height: 740 } });
    await signIn(alice, 'alice', '/dashboard');
    const tabs = alice.getByRole('navigation', { name: 'Onglets' });
    for (const name of ['Accueil', 'Placements', 'Abonnements', 'Routine', 'Plus'])
      await expect(tabs.getByRole('link', { name })).toBeInViewport({ ratio: 1 });
    expect(await alice.evaluate('document.documentElement.scrollWidth <= window.innerWidth')).toBe(true);
    // Issue 374: the person menu sits in the top bar on a phone too, on the bar's one row.
    const avatar = alice.getByRole('banner').getByRole('button', { name: 'Alice Dev' });
    await expect(avatar).toBeInViewport({ ratio: 1 });
    const [box, bar] = await Promise.all([avatar.boundingBox(), alice.locator('[data-pane="toolbar"]').boundingBox()]);
    expect(box!.x + box!.width).toBeLessThanOrEqual(bar!.x + bar!.width);

    await tabs.getByRole('link', { name: 'Plus' }).click();
    await expect(alice.getByRole('heading', { name: 'Plus', level: 1 })).toBeVisible();
    await expect(alice.getByRole('list', { name: 'Autres sections' }).getByRole('link', { name: 'Réglages' })).toBeVisible();
    await expect(alice.getByRole('button', { name: 'Se déconnecter' })).toBeVisible();
  });
}

// Account pages live in the person menu at the end of the top bar (a lazy chunk: wait for it to open).
test('the person menu leads to the account pages', async ({ browser }) => {
  const alice = await browser.newPage({ viewport: { width: 1280, height: 800 } });
  await signIn(alice, 'alice', '/dashboard');
  for (const [item, heading] of [['Sécurité', 'Sécurité'], ['Import', 'Import']]) {
    const trigger = alice.getByRole('banner').getByRole('button', { name: 'Alice Dev' });
    await trigger.click();
    // The open menu is modal: the bar behind it, trigger included, leaves the accessibility tree.
    await expect(alice.getByRole('menu')).toBeVisible();
    await alice.getByRole('menuitem', { name: item }).click();
    await expect(alice.getByRole('heading', { name: heading, level: 1 })).toBeVisible();
  }
});

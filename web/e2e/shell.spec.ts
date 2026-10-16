import { expect, test } from '@playwright/test';
import { signIn } from './session';

// 2E1: on a phone, every tab — « Plus » included — fits on screen; jsdom cannot see a layout, so this runs in a browser.
for (const width of [320, 375, 414]) {
  test(`the bottom tabs fit a ${width}px phone and « Plus » leads to the other sections`, async ({ browser }) => {
    const alice = await browser.newPage({ viewport: { width, height: 740 } });
    await signIn(alice, 'alice', '/dashboard');
    const tabs = alice.getByRole('navigation', { name: 'Onglets' });
    for (const name of ['Accueil', 'Abonnements', 'Placements', 'Revenus', 'Plus'])
      await expect(tabs.getByRole('link', { name })).toBeInViewport({ ratio: 1 });
    // The header wraps rather than push « Se déconnecter » off a narrow screen.
    await expect(alice.getByRole('button', { name: 'Se déconnecter' })).toBeInViewport({ ratio: 1 });
    expect(await alice.evaluate('document.documentElement.scrollWidth <= window.innerWidth')).toBe(true);

    await tabs.getByRole('link', { name: 'Plus' }).click();
    await expect(alice.getByRole('heading', { name: 'Plus', level: 1 })).toBeVisible();
    await expect(alice.getByRole('list', { name: 'Autres sections' }).getByRole('link', { name: 'Réglages' })).toBeVisible();
  });
}

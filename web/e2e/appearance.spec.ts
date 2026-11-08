import { expect, test, type Page } from '@playwright/test';
import { signIn } from './session';

// The glass layers need a real browser: jsdom computes no backdrop-filter, no media query and no focus scrolling.
test.use({ viewport: { width: 1280, height: 800 } });

// The e2e tsconfig has no DOM lib: the page code goes in as a string, as in the other specs.
const OPAQUE = `(() => {
  const style = getComputedStyle(document.querySelector('[data-pane="sidebar"]'));
  const color = style.backgroundColor;
  const slash = /\\/\\s*([\\d.]+)(%?)\\s*\\)$/.exec(color);
  const comma = /^rgba\\(.*,\\s*([\\d.]+)\\s*\\)$/.exec(color);
  const alpha = slash ? Number(slash[1]) / (slash[2] ? 100 : 1) : comma ? Number(comma[1]) : 1;
  return { alpha, backdrop: style.backdropFilter, halo: getComputedStyle(document.body, '::before').backgroundImage };
})()`;

/** The sidebar's background alpha (1 when the colour has none), its backdrop-filter and the halo layer's background-image. */
const opaque = (page: Page) => page.evaluate<{ alpha: number; backdrop: string; halo: string }>(OPAQUE);

async function expectOpaque(page: Page) {
  await expect.poll(() => opaque(page)).toEqual({ alpha: 1, backdrop: 'none', halo: 'none' });
}

test('by default the sidebar is translucent glass over the halos', async ({ page }) => {
  await signIn(page, 'alice', '/dashboard');
  // Read once the shell is drawn: right after sign-in the sidebar may not be in the page yet.
  await expect(page.locator('[data-pane="sidebar"]')).toBeVisible();
  const { alpha, backdrop, halo } = await opaque(page);
  expect(alpha).toBeLessThan(1);
  expect(backdrop).not.toBe('none');
  expect(halo).not.toBe('none');
});

test.describe('fallbacks', () => {
  for (const [name, media] of [['Increase Contrast', { contrast: 'more' }], ['forced colours', { forcedColors: 'active' }]] as const) {
    test(`${name} makes the surfaces opaque`, async ({ page }) => {
      await signIn(page, 'alice', '/dashboard');
      await expect(page.locator('[data-pane="sidebar"]')).toBeVisible();
      await page.emulateMedia(media);
      await expectOpaque(page);
    });
  }
});

test('« Réduire la transparence » makes them opaque and survives a reload', async ({ page }) => {
  await signIn(page, 'alice', '/settings/appearance');
  await page.getByRole('switch', { name: 'Réduire la transparence' }).click();
  await expectOpaque(page);

  await page.reload({ waitUntil: 'domcontentloaded' });
  // Before React renders anything, public/theme-init.js has already applied the choice (no flash).
  expect(await page.evaluate<boolean>(`document.documentElement.hasAttribute('data-reduce-transparency')`)).toBe(true);
  expect(await page.evaluate<string>(`getComputedStyle(document.documentElement).getPropertyValue('--halo').trim()`)).toBe('none');
  await expect(page.locator('[data-pane="sidebar"]')).toBeVisible();
  await expectOpaque(page);
});

test('a focused element is never hidden under the floating top bar', async ({ page }) => {
  await signIn(page, 'alice', '/dashboard');
  // A list long enough to scroll, whatever the other journeys created before this one: twenty envelopes of its own.
  const stamp = Date.now();
  const session = (await (await page.request.get('/auth/session')).json()) as { csrf: string; person: { id: string } };
  for (let i = 1; i <= 20; i++) {
    const created = await page.request.post('/api/v1/investments/envelopes', {
      headers: { 'X-Nid-Csrf': session.csrf },
      data: {
        displayName: `Défilement ${stamp} ${i}`, kind: 'savings', currency: 'EUR', visibility: 'private',
        owners: [{ personId: session.person.id, shareBp: 10000 }], firstValuation: { asOf: '2026-01-01', amount: { amount: '1000', currency: 'EUR' } },
      },
    });
    expect(created.status()).toBe(201);
  }
  await page.goto('/investments?tab=list&scope=me'); // private envelopes: alice's own view lists them, the household's does not
  // The table's links: the phone list below it holds the same links, hidden on a wide screen.
  const links = page.locator('main').getByRole('table').getByRole('link');
  await expect(links.first()).toBeVisible();
  const viewport = page.viewportSize()!;
  const state = () =>
    page.evaluate<{ top: number; toolbarBottom: number; scrollY: number }>(
      `({ top: document.activeElement.getBoundingClientRect().top, toolbarBottom: document.querySelector('[data-pane="toolbar"]').getBoundingClientRect().bottom, scrollY: window.scrollY })`,
    );

  await links.last().focus();
  const low = await state();
  expect(low.top, 'focus is low on the page').toBeGreaterThan(viewport.height / 2);
  expect(low.scrollY, 'the page scrolled to it').toBeGreaterThan(0);

  // Going back up scrolls each focused element into view: none may land under the bar.
  for (let i = 0; i < 6; i++) {
    await page.keyboard.press('Shift+Tab');
    const { top, toolbarBottom } = await state();
    expect(top).toBeGreaterThanOrEqual(toolbarBottom);
  }
});

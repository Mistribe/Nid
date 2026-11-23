// Captures each feature area's screen from the demo household, in light and in dark, for the site's feature pages
// (src/assets/screens/<slug>-{light,dark}.png, 1372×870 like docs/frontend/screens.md §13). Signs in as Alice, the demo's
// invented adult, so no real household ever appears. Usage: pnpm --dir site capture [--base https://demo.nid.mistribe.com]
import { fileURLToPath } from 'node:url';
import { areas } from '../src/data/areas.ts';

const VIEWPORT = { width: 1372, height: 870 };

/** @param {string} base @returns {Array<{ url: string, file: string, colorScheme: 'light' | 'dark' }>} */
export function shots(base) {
  const origin = base.replace(/\/+$/, '');
  return ['light', 'dark'].flatMap((colorScheme) =>
    areas.map(({ slug, route }) => ({ url: `${origin}${route}`, file: `src/assets/screens/${slug}-${colorScheme}.png`, colorScheme })),
  );
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const { chromium } = await import('playwright');
  const at = process.argv.indexOf('--base');
  const base = (at > 0 ? process.argv[at + 1] : 'https://demo.nid.mistribe.com').replace(/\/+$/, '');
  const root = fileURLToPath(new URL('..', import.meta.url));
  const browser = await chromium.launch();
  try {
    for (const colorScheme of ['light', 'dark']) {
      // The app's mode defaults to « system »: the context's colour scheme picks the theme.
      const context = await browser.newContext({ viewport: VIEWPORT, colorScheme, locale: 'fr-FR' });
      const page = await context.newPage();
      await page.goto(`${base}/auth/demo/alice?returnUrl=${encodeURIComponent('/dashboard')}`, { waitUntil: 'networkidle' });
      if (!new URL(page.url()).pathname.startsWith('/dashboard')) throw new Error(`Demo sign-in did not land on the dashboard: ${page.url()}`);
      for (const shot of shots(base).filter((s) => s.colorScheme === colorScheme)) {
        await page.goto(shot.url, { waitUntil: 'networkidle' });
        await page.screenshot({ path: `${root}${shot.file}` });
        console.log(shot.file);
      }
      await context.close();
    }
  } finally {
    await browser.close();
  }
}

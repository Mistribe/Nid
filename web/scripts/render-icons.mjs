// Renders the touch and app icons from the nest: `node scripts/render-icons.mjs` (from web/).
import { chromium } from '@playwright/test';

// The ardoise theme's light primary, oklch(0.55 0.2 256).
const TILE = '#006ce2';
// [file, size, share of the tile the mark fills]; the maskable icon keeps the mark in the 60 % safe zone.
const ICONS = [
  ['apple-touch-icon.png', 180, 0.75],
  ['icon-192.png', 192, 0.75],
  ['icon-512.png', 512, 0.75],
  ['icon-maskable-512.png', 512, 0.6],
];

const mark = (px) => `<svg width="${px}" height="${px}" viewBox="0 0 24 24" fill="none" stroke="#fff" stroke-width="2" stroke-linecap="round">
  <path d="M3 12.5 Q12 23 21 12.5"/><path d="M6.5 14 Q12 19.5 17.5 14"/>
  <ellipse cx="12" cy="9" rx="3" ry="3.8" fill="#fff" stroke="none"/></svg>`;

const browser = await chromium.launch();
for (const [file, size, share] of ICONS) {
  const page = await browser.newPage({ viewport: { width: size, height: size } });
  await page.setContent(`<body style="margin:0;width:${size}px;height:${size}px;background:${TILE};display:grid;place-items:center">${mark(size * share)}</body>`);
  await page.screenshot({ path: `public/${file}`, clip: { x: 0, y: 0, width: size, height: size } });
  await page.close();
}
await browser.close();

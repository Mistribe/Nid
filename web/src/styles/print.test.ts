import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { themes } from './themes';

const dir = dirname(fileURLToPath(import.meta.url));
const css = readFileSync(join(dir, 'globals.css'), 'utf8');
const noComments = (source: string) => source.replace(/\/\*[\s\S]*?\*\//g, '');

// What paper gets (docs/frontend/screens.md, « Bilan annuel »); web/e2e/annual-review.spec.ts prints the page itself.
describe('print', () => {
  it('prints on A4 with 15 mm margins', () => {
    expect(css).toMatch(/@page\s*\{\s*size:\s*A4;\s*margin:\s*15mm;\s*\}/);
  });

  // The page's last print block: the layout rules.
  const print = noComments(css.slice(css.lastIndexOf('@media print')));

  it('keeps a row, a line of figures and a chart on one page, a section title with what follows, not a whole section', () => {
    expect(print).toMatch(/tr,\s*\[data-stat-line\],\s*\[data-slot=chart\]\s*\{\s*break-inside:\s*avoid;\s*\}/);
    expect(print).toMatch(/section > h2,\s*section > :has\(> h2\)\s*\{\s*break-after:\s*avoid;\s*\}/);
    expect(print).not.toMatch(/(^|[\s,{])section\s*[,{]/);
  });

  it('fits tables and charts in the page: cells wrap, nothing scrolls out of sight', () => {
    expect(print).toMatch(/\[data-slot=table-container\]\s*\{\s*overflow:\s*visible;\s*\}/);
    expect(print).toMatch(/\[data-slot=table-head\],\s*\[data-slot=table-cell\]\s*\{\s*white-space:\s*normal;\s*\}/);
    expect(print).toMatch(/\.recharts-surface\s*\{\s*max-width:\s*100%;/);
  });

  // A dark screen still prints dark text on white: the dark colours, color-scheme and the dark: variant are screen-only.
  const offScreen = (source: string) => noComments(source).replace(/@media screen\s*\{(?:[^{}]|\{(?:[^{}]|\{[^{}]*\})*\})*\}/g, '');

  it('keeps the dark tokens, color-scheme and the dark: variant to the screen', () => {
    expect(css).toMatch(/@custom-variant dark\s*\{\s*@media screen\s*\{/);
    expect(offScreen(css)).not.toContain('--chart-1: #4488dc');
    expect(offScreen(css)).not.toContain('color-scheme: dark');
  });

  it.each(themes.map((t) => t.id))('%s applies its dark colours on screen only', (id) => {
    const source = readFileSync(join(dir, 'themes', `${id}.css`), 'utf8');
    expect(source).toMatch(/\.dark\s*\{/);
    expect(offScreen(source)).not.toContain('.dark');
  });
});

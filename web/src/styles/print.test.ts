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

  it('keeps a section and a table row on one page, and a chart within the page', () => {
    expect(css).toMatch(/@media print\s*\{[^@]*section,\s*tr\s*\{\s*break-inside:\s*avoid;\s*\}/);
    expect(css).toMatch(/\.recharts-surface\s*\{\s*max-width:\s*100%;/);
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

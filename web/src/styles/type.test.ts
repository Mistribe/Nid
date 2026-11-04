import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

const css = readFileSync(join(dirname(fileURLToPath(import.meta.url)), 'globals.css'), 'utf8');

// The block opened by `selector`, found with a brace counter: a media block nests a rule.
function block(selector: string): string {
  const start = css.indexOf(`${selector} {`);
  if (start < 0) return '';
  const open = css.indexOf('{', start);
  let depth = 0;
  for (let i = open; i < css.length; i++) {
    if (css[i] === '{') depth++;
    if (css[i] === '}' && --depth === 0) return css.slice(open + 1, i);
  }
  return '';
}

describe('type scale', () => {
  it('defines the type scale in @theme', () => {
    const theme = block('@theme');
    for (const t of [
      '--text-large-title: 2rem', '--text-large-title--font-weight: 800', '--text-large-title--letter-spacing: -0.025em',
      '--text-title: 1.125rem', '--text-title--font-weight: 650', '--text-hero: 2.5rem', '--text-hero--font-weight: 700',
      '--text-figure: 1.25rem', '--text-label: 0.8125rem',
      '--text-title--line-height: 1.4', '--text-figure--line-height: 1.3', '--text-label--line-height: 1.4',
    ]) expect(theme).toContain(t);
  });

  it('shrinks the titles and figures on a phone', () => {
    const phone = block('@media (width < 48rem)');
    for (const t of ['--text-large-title: 1.75rem', '--text-hero: 2rem', '--text-figure: 1.125rem']) expect(phone).toContain(t);
    expect(phone).toContain(':root');
  });
});

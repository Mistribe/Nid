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

describe('layers', () => {
  it('the :root and .dark blocks define the layer tokens', () => {
    const root = /^:root\s*\{([^}]*)\}/m.exec(css)![1];
    const dark = /^\.dark\s*\{([^}]*)\}/m.exec(css)![1];
    for (const t of ['--material-opacity: 80%', '--glass-opacity: 55%', '--glass-regular-opacity: 85%']) expect(root).toContain(t);
    expect(dark).toContain('--glass-opacity: 45%');
  });

  it.each([
    '@media (prefers-reduced-transparency: reduce), (prefers-contrast: more), (forced-colors: active)',
    ':root[data-reduce-transparency]',
    '@media print',
  ])('%s makes every surface opaque', (selector) => {
    const b = block(selector);
    for (const t of [
      '--material-opacity: 100%', '--glass-opacity: 100%', '--glass-regular-opacity: 100%',
      '--backdrop-material: none', '--backdrop-glass: none', '--backdrop-edge: none', '--halo: none',
    ]) expect(b).toContain(t);
  });

  it('Increase Contrast turns muted text into foreground and borders into input', () => {
    const b = block('@media (prefers-contrast: more)');
    expect(b).toContain('--muted-foreground: var(--foreground)');
    expect(b).toContain('--border: var(--input)');
    expect(b).toContain('--glass-edge: var(--input)');
  });

  it('glass-regular colours the border its overlay draws, never adds one (a sheet keeps its single edge)', () => {
    const b = block('@utility glass-regular');
    expect(b).toContain('border-color: var(--glass-edge)');
    expect(b).not.toMatch(/border:/);
  });

  // tailwind-merge files them as background colours (lib/utils.ts): the shorthand would also reset a `bg-linear-…` image.
  it.each(['material', 'glass', 'glass-regular', 'content-pane'])('%s sets the background colour only', (layer) => {
    const b = block(`@utility ${layer}`);
    expect(b).toContain('background-color: color-mix(');
    expect(b).not.toMatch(/background:/);
  });
});

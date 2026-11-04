import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

const css = readFileSync(join(dirname(fileURLToPath(import.meta.url)), 'globals.css'), 'utf8');

// Where `selector` opens its block, whatever the whitespace before the brace.
function at(selector: string, source = css): number {
  return source.search(new RegExp(`${selector.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}\\s*\\{`));
}

// The block opened by `selector`, found with a brace counter: a media block nests a rule.
function block(selector: string, source = css): string {
  const start = at(selector, source);
  if (start < 0) return '';
  const open = source.indexOf('{', start);
  let depth = 0;
  for (let i = open; i < source.length; i++) {
    if (source[i] === '{') depth++;
    if (source[i] === '}' && --depth === 0) return source.slice(open + 1, i);
  }
  return '';
}

// A block's custom properties, sorted, its comments left out.
const tokens = (b: string) => [...b.replace(/\/\*[\s\S]*?\*\//g, '').matchAll(/(--[\w-]+):\s*([^;]+);/g)].map((m) => `${m[1]}: ${m[2]!.trim()}`).sort();

const OPAQUE = [
  '@media (prefers-reduced-transparency: reduce), (prefers-contrast: more), (forced-colors: active)',
  ':root[data-reduce-transparency]',
  '@media print',
];

describe('layers', () => {
  it('the :root and .dark blocks define the layer tokens', () => {
    const root = /^:root\s*\{([^}]*)\}/m.exec(css)![1];
    const dark = /^\.dark\s*\{([^}]*)\}/m.exec(css)![1];
    for (const t of ['--material-opacity: 80%', '--glass-opacity: 55%', '--glass-regular-opacity: 85%']) expect(root).toContain(t);
    expect(dark).toContain('--glass-opacity: 45%');
  });

  it.each(OPAQUE)('%s makes every surface opaque', (selector) => {
    const b = block(selector);
    for (const t of [
      '--material-opacity: 100%', '--glass-opacity: 100%', '--glass-regular-opacity: 100%',
      '--backdrop-material: none', '--backdrop-glass: none', '--backdrop-edge: none', '--halo: none',
    ]) expect(b).toContain(t);
  });

  it('block() finds a selector whatever the whitespace before its brace', () => {
    for (const source of ['.x{--a: 1;}', '.x {--a: 1;}', '.x\n  {--a: 1;}']) expect(block('.x', source)).toBe('--a: 1;');
  });

  // Same specificity as the tokens they override: they win by coming later.
  it.each(OPAQUE)('%s comes after the theme imports and every .dark block', (selector) => {
    const start = at(selector);
    expect(start).toBeGreaterThan(0);
    for (const theme of css.matchAll(/^@import\b/gm)) expect(start).toBeGreaterThan(theme.index);
    for (const dark of css.matchAll(/^\.dark\s*\{/gm)) expect(start).toBeGreaterThan(dark.index);
  });

  // CSS cannot share a declaration list between a media query and an attribute selector: the three copies stay equal.
  it('the three opaque token sets are the same', () => {
    const [first, ...rest] = OPAQUE.map((selector) => tokens(block(selector)));
    expect(first!.length).toBeGreaterThan(0);
    for (const copy of rest) expect(copy).toEqual(first);
  });

  // A transform kept after the end would make every page block the containing block of its `position: fixed` children.
  it('the entry animation leaves no transform once it ends', () => {
    const animation = /--animate-enter:\s*([^;]+);/.exec(css)![1]!;
    expect(animation).not.toMatch(/\b(forwards|both)\b/);
    const keyframes = block('@keyframes enter');
    expect(keyframes).toContain('transform: translateY(6px)');
    // The last keyframe is the element's own style unless written out, and then it must be `transform: none`.
    const end = /(?:\bto|100%)\s*\{([^}]*)\}/.exec(keyframes)?.[1];
    if (end !== undefined) expect(end).toMatch(/transform:\s*none/);
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

  it('defines --separator and --fill in both modes', () => {
    const root = /^:root\s*\{([^}]*)\}/m.exec(css)![1];
    const dark = /^\.dark\s*\{([^}]*)\}/m.exec(css)![1];
    expect(root).toContain('--separator: oklch(0.4 0.01 265 / .12)');
    expect(root).toContain('--fill: oklch(0.6 0.01 265 / .12)');
    expect(dark).toContain('--separator: oklch(0.45 0.01 265 / .6)');
    expect(dark).toContain('--fill: oklch(0.6 0.01 265 / .24)');
    expect(css).toContain('--color-separator: var(--separator)');
    expect(css).toContain('--color-fill: var(--fill)');
  });

  it('makes separators the border under Increase Contrast', () => {
    expect(block('@media (prefers-contrast: more)')).toContain('--separator: var(--border)');
  });
});

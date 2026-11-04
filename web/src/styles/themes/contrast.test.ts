import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { differenceEuclidean, formatHex, interpolate, parse, rgb, wcagContrast } from 'culori';
import { describe, expect, it } from 'vitest';
import { PALETTE } from '@/lib/labelColour';
import { themes } from './index';

// frontend/design-system.md §7.7, with `input` for a field's outline (plan Task 6 ruling) and four more pairs.
const PAIRS: [foreground: string, background: string, minimum: number][] = [
  ['foreground', 'background', 4.5],
  ['foreground', 'card', 4.5],
  ['foreground', 'popover', 4.5],
  ['card-foreground', 'card', 4.5],
  ['popover-foreground', 'popover', 4.5],
  ['primary-foreground', 'primary', 4.5],
  ['muted-foreground', 'background', 4.5],
  ['muted-foreground', 'card', 4.5],
  ['secondary-foreground', 'secondary', 4.5],
  ['accent-foreground', 'accent', 4.5],
  ['destructive', 'background', 4.5],
  ['status-warning-text', 'background', 4.5],
  ['status-warning-text', 'card', 4.5],
  ['status-good-text', 'background', 4.5],
  ['status-good-text', 'card', 4.5],
  ['input', 'background', 3],
  ['ring', 'background', 3],
  ['primary', 'background', 4.5],
];

// Layers over the halo ground (visual identity spec §3.3): [text, surface, opacity variable of the surface, and a tint
// laid over the surface: its colour and the alpha of its `bg-x/N` class].
const COMPOSITE: [text: string, surface: string, opacity: string, tint?: [colour: string, alpha: number]][] = [
  ['foreground', 'card', 'material-opacity'],
  ['muted-foreground', 'card', 'material-opacity'],
  ['card-foreground', 'card', 'material-opacity'],
  // Text on the content window (`content-pane`): the accents a page writes in.
  ['destructive', 'card', 'material-opacity'],
  ['status-good-text', 'card', 'material-opacity'],
  ['status-warning-text', 'card', 'material-opacity'],
  ['primary', 'card', 'material-opacity'],
  ['foreground', 'glass', 'glass-opacity'],
  ['muted-foreground', 'glass', 'glass-opacity'],
  ['sidebar-foreground', 'glass', 'glass-opacity'],
  ['popover-foreground', 'popover', 'glass-regular-opacity'],
  ['muted-foreground', 'popover', 'glass-regular-opacity'],
  // The rail's initials (Shell, PersonMenu: `bg-primary/15`) and the critical banner (`bg-status-warning/10` on material).
  ['foreground', 'glass', 'glass-opacity', ['primary', 0.15]],
  ['foreground', 'card', 'material-opacity', ['status-warning', 0.1]],
];

// Composites in sRGB: a·top + (1 − a)·bottom per channel.
function over(top: string, alpha: number, bottom: string): string {
  const t = rgb(top)!;
  const b = rgb(bottom)!;
  const mix = (x: number, y: number) => alpha * x + (1 - alpha) * y;
  return formatHex({ mode: 'rgb', r: mix(t.r, b.r), g: mix(t.g, b.g), b: mix(t.b, b.b) });
}

function block(css: string, selector: RegExp): Record<string, string> {
  const match = selector.exec(css);
  if (!match?.[1]) throw new Error(`No block matches ${String(selector)}`);
  return Object.fromEntries([...match[1].matchAll(/--([\w-]+):\s*([^;]+);/g)].map((m) => [m[1]!, m[2]!.trim()]));
}

// `join` avoids the `new URL(x, import.meta.url)` pattern: Vite's client asset transform intercepts that
// exact shape (even for a template literal) once the test worker has any `setupFiles` entry, rewriting the
// base to the jsdom document location (`http://localhost:3000/…`) instead of leaving it as a real file path.
const THEMES_DIR = dirname(fileURLToPath(import.meta.url));

// The fixed colours of globals.css (status text), under each theme's own.
const globals = readFileSync(join(THEMES_DIR, '..', 'globals.css'), 'utf8');
const fixed = { light: block(globals, /^:root\s*\{([^}]*)\}/m), dark: block(globals, /^\.dark\s*\{([^}]*)\}/m) };

function variables(id: string) {
  const css = readFileSync(join(THEMES_DIR, `${id}.css`), 'utf8');
  return {
    light: { ...fixed.light, ...block(css, new RegExp(`\\[data-theme="${id}"\\]\\s*\\{([^}]*)\\}`)) },
    dark: { ...fixed.light, ...fixed.dark, ...block(css, new RegExp(`\\[data-theme="${id}"\\]\\.dark\\s*\\{([^}]*)\\}`)) },
  };
}

// The tile's top light, read from ModuleTile.tsx (`before:from-white/N`, `before:to-S%`): its opacity where the glyph
// starts, 19 % down (a 62 % glyph, centred), the gradient fading linearly to its transparent stop.
const tileSource = readFileSync(join(THEMES_DIR, '..', '..', 'components', 'ModuleTile.tsx'), 'utf8');
const GLYPH_TOP = 19;
const GLOSS = Number(/before:from-white\/(\d+)/.exec(tileSource)![1]) / 100;
const GLOSS_END = Number(/before:to-(\d+)%/.exec(tileSource)?.[1] ?? 100);
const GLOSS_AT_GLYPH = GLOSS * Math.max(0, 1 - GLYPH_TOP / GLOSS_END);

// The share of the slot colour in a monogram chip, read from ProviderIcon.tsx (`color-mix(in oklab, … N%, …)`).
const CHIP_TINT = Number(/color-mix\(in oklab, \$\{[^}]+\} (\d+)%/.exec(readFileSync(join(THEMES_DIR, '..', '..', 'components', 'ProviderIcon.tsx'), 'utf8'))![1]) / 100;

it('over() blends in sRGB', () => {
  expect(differenceEuclidean('rgb')(over('#ffffff', 0.5, '#000000'), '#808080')).toBeLessThan(0.01);
});

describe.each(themes.map((t) => t.id))('theme %s', (id) => {
  describe.each(['light', 'dark'] as const)('%s mode', (mode) => {
    const vars = variables(id)[mode];

    it.each(PAIRS)('%s on %s reaches %s:1', (foreground, background, minimum) => {
      const fg = vars[foreground];
      const bg = vars[background];
      expect(fg, `--${foreground}`).toBeDefined();
      expect(bg, `--${background}`).toBeDefined();
      for (const value of [fg!, bg!]) expect(parse(value)?.alpha ?? 1, `${value} must be opaque to be measured`).toBe(1);
      expect(wcagContrast(fg!, bg!)).toBeGreaterThanOrEqual(minimum);
    });

    // A `var(--x)` value resolves once to `vars[x]`.
    const resolve = (name: string) => {
      const value = vars[name]!;
      const ref = /^var\(--([\w-]+)\)$/.exec(value);
      return ref ? vars[ref[1]!]! : value;
    };

    // A module tile's glyph under the tile's top light, where the glyph starts (WCAG 1.4.11, visual identity spec §3.6):
    // white on a slot colour, `--primary-foreground` on the `primary` tile.
    it.each([
      ['tile-1', '#ffffff'], ['tile-2', '#ffffff'], ['tile-3', '#ffffff'], ['tile-4', '#ffffff'], ['tile-5', '#ffffff'],
      ['tile-6', '#ffffff'], ['tile-7', '#ffffff'], ['tile-8', '#ffffff'], ['tile-neutral', '#ffffff'],
      ['primary', 'primary-foreground'],
    ])('a glyph on %s reaches 3:1', (tile, glyph) => {
      const glyphColour = glyph.startsWith('#') ? glyph : formatHex(resolve(glyph))!;
      expect(wcagContrast(glyphColour, over('#ffffff', GLOSS_AT_GLYPH, formatHex(resolve(tile))!))).toBeGreaterThanOrEqual(3);
    });

    // A monogram chip (ProviderIcon): its slot colour at CHIP_TINT over the card, the initials in the text colour.
    it.each([1, 2, 3, 4, 5, 6, 7, 8, 'neutral'])('foreground on a %s chip reaches 4.5:1', (slot) => {
      // `color-mix(in oklab, …)`: the blend is in OKLab, not sRGB.
      const chip = formatHex(interpolate([formatHex(vars['card'])!, formatHex(vars[slot === 'neutral' ? 'muted-foreground' : `chart-${slot}`])!], 'oklab')(CHIP_TINT));
      expect(wcagContrast(formatHex(vars['foreground'])!, chip)).toBeGreaterThanOrEqual(4.5);
    });

    if (mode === 'light')
      it('labelColour keeps the light --chart-1…8 palette', () => {
        expect(PALETTE).toEqual([1, 2, 3, 4, 5, 6, 7, 8].map((n) => formatHex(vars[`chart-${n}`])));
      });

    it('defines an opaque --glass and four halos', () => {
      expect(parse(vars['glass']!), '--glass').toBeDefined();
      expect(parse(vars['glass']!)?.alpha ?? 1).toBe(1);
      for (const n of [1, 2, 3, 4]) expect(parse(vars[`halo-${n}`]!), `--halo-${n}`).toBeDefined();
    });

    it.each(COMPOSITE)('%s on %s over every halo reaches 4.5:1 (%s, tint %j)', (text, surface, opacity, tint) => {
      const background = formatHex(vars['background'])!;
      const grounds = [background];
      for (const n of [1, 2, 3, 4]) {
        const halo = parse(vars[`halo-${n}`]!)!;
        grounds.push(over(formatHex(halo), halo.alpha ?? 1, background));
      }
      const alpha = parseFloat(vars[opacity]!) / 100;
      for (const ground of grounds) {
        const pane = over(formatHex(resolve(surface))!, alpha, ground);
        const composite = tint ? over(formatHex(resolve(tint[0]))!, tint[1], pane) : pane;
        expect(wcagContrast(formatHex(resolve(text))!, composite), `${text} on ${surface} over ${ground}`).toBeGreaterThanOrEqual(4.5);
      }
    });

    it('foreground reaches 7:1 on background and card', () => {
      for (const surface of ['background', 'card']) expect(wcagContrast(vars['foreground']!, vars[surface]!)).toBeGreaterThanOrEqual(7);
    });

    if (id === 'ardoise') {
      it('ardoise is not Tailwind slate and blue', () => {
        if (mode === 'dark') expect(vars['background']).not.toBe('oklch(0.129 0.042 264.7)');
        expect(['oklch(0.623 0.188 259.8)', 'oklch(0.546 0.215 262.9)']).not.toContain(vars['primary']);
      });
    }
  });
});

import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { differenceEuclidean, formatHex, parse, rgb, wcagContrast } from 'culori';
import { describe, expect, it } from 'vitest';
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

// Layers over the halo ground (visual identity spec §3.3): [text, surface, opacity variable of the surface].
const COMPOSITE: [text: string, surface: string, opacity: string][] = [
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

    it('defines an opaque --glass and four halos', () => {
      expect(parse(vars['glass']!), '--glass').toBeDefined();
      expect(parse(vars['glass']!)?.alpha ?? 1).toBe(1);
      for (const n of [1, 2, 3, 4]) expect(parse(vars[`halo-${n}`]!), `--halo-${n}`).toBeDefined();
    });

    it.each(COMPOSITE)('%s on %s over every halo reaches 4.5:1', (text, surface, opacity) => {
      const background = formatHex(vars['background'])!;
      const grounds = [background];
      for (const n of [1, 2, 3, 4]) {
        const halo = parse(vars[`halo-${n}`]!)!;
        grounds.push(over(formatHex(halo), halo.alpha ?? 1, background));
      }
      const alpha = parseFloat(vars[opacity]!) / 100;
      for (const ground of grounds) {
        const composite = over(formatHex(resolve(surface))!, alpha, ground);
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

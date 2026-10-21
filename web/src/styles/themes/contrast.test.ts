import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parse, wcagContrast } from 'culori';
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
];

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
  });
});

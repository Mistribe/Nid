import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { applyTheme, rememberTheme, resolveMode, resolveTheme } from './theme';

const media = (matches: boolean) => ({ matches }) as MediaQueryList;
const root = () => document.documentElement;

describe('applyTheme', () => {
  it('sets the theme attribute and the dark class', () => {
    applyTheme('sauge', 'dark', root(), media(false));
    expect(root().dataset.theme).toBe('sauge');
    expect(root().classList.contains('dark')).toBe(true);
  });

  it('follows the system in system mode', () => {
    applyTheme('neutre', 'system', root(), media(true));
    expect(root().classList.contains('dark')).toBe(true);
    applyTheme('neutre', 'system', root(), media(false));
    expect(root().classList.contains('dark')).toBe(false);
  });
});

describe('resolving stored values', () => {
  it('falls back to neutre for a theme this build does not ship', () => expect(resolveTheme('ocean')).toBe('neutre'));
  it('falls back to system for an unknown mode', () => expect(resolveMode('sepia')).toBe('system'));
});

describe('theme-init.js', () => {
  // `join` avoids the `new URL(x, import.meta.url)` pattern: Vite's client asset transform intercepts that
  // exact shape once the test worker has any `setupFiles` entry, rewriting the base to the jsdom document
  // location (`http://localhost:3000/…`) instead of leaving it as a real file path.
  const source = readFileSync(join(dirname(fileURLToPath(import.meta.url)), '../../public/theme-init.js'), 'utf8');

  it('applies the remembered theme and mode before React renders', () => {
    rememberTheme('ardoise', 'dark');
    // eslint-disable-next-line @typescript-eslint/no-implied-eval, @typescript-eslint/no-unsafe-call -- runs public/theme-init.js exactly as the browser does
    new Function(source)();
    expect(root().dataset.theme).toBe('ardoise');
    expect(root().classList.contains('dark')).toBe(true);
  });

  it('refuses a stored value that is not a theme identifier', () => {
    localStorage.setItem('nid.theme', '"><script>');
    // eslint-disable-next-line @typescript-eslint/no-implied-eval, @typescript-eslint/no-unsafe-call -- runs public/theme-init.js exactly as the browser does
    new Function(source)();
    expect(root().dataset.theme).toBe('neutre');
  });
});

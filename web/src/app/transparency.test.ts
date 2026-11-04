import { act, renderHook } from '@testing-library/react';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { TRANSPARENCY_KEY, useReduceTransparency } from './transparency';

const marked = () => document.documentElement.hasAttribute('data-reduce-transparency');

afterEach(() => {
  document.documentElement.removeAttribute('data-reduce-transparency');
  vi.restoreAllMocks();
});

describe('useReduceTransparency', () => {
  it('starts from the device choice', () => {
    localStorage.setItem(TRANSPARENCY_KEY, 'reduce');
    expect(renderHook(() => useReduceTransparency()).result.current.reduced).toBe(true);
  });

  it('setReduced(true) stores the choice and marks <html>', () => {
    const { result } = renderHook(() => useReduceTransparency());
    act(() => result.current.setReduced(true));
    expect(localStorage.getItem(TRANSPARENCY_KEY)).toBe('reduce');
    expect(marked()).toBe(true);
    expect(result.current.reduced).toBe(true);
  });

  it('setReduced(false) clears both', () => {
    localStorage.setItem(TRANSPARENCY_KEY, 'reduce');
    document.documentElement.setAttribute('data-reduce-transparency', '');
    const { result } = renderHook(() => useReduceTransparency());
    act(() => result.current.setReduced(false));
    expect(localStorage.getItem(TRANSPARENCY_KEY)).toBeNull();
    expect(marked()).toBe(false);
    expect(result.current.reduced).toBe(false);
  });

  it('keeps working when storage is refused', () => {
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('refused'); });
    const { result } = renderHook(() => useReduceTransparency());
    act(() => result.current.setReduced(true));
    expect(marked()).toBe(true);
    expect(result.current.reduced).toBe(true);
  });
});

describe('theme-init.js', () => {
  const source = readFileSync(join(dirname(fileURLToPath(import.meta.url)), '../../public/theme-init.js'), 'utf8');
  // eslint-disable-next-line @typescript-eslint/no-implied-eval, @typescript-eslint/no-unsafe-call -- runs public/theme-init.js exactly as the browser does
  const run = () => { new Function(source)(); };

  it('theme-init.js marks <html> before the first render', () => {
    run();
    expect(marked()).toBe(false);
    localStorage.setItem(TRANSPARENCY_KEY, 'reduce');
    run();
    expect(marked()).toBe(true);
  });
});

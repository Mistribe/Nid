import { afterEach, beforeEach, vi, type MockInstance } from 'vitest';

/**
 * Recharts grows its marks from zero unless motion is reduced: a suite that reads a mark's shape or colour calls this
 * in its `describe`, so the marks are drawn at once.
 */
export function reduceMotion() {
  let spy: MockInstance<typeof window.matchMedia> | undefined;
  beforeEach(() => {
    const matchMedia = window.matchMedia.bind(window);
    spy = vi.spyOn(window, 'matchMedia').mockImplementation((query) => ({ ...matchMedia(query), matches: query === '(prefers-reduced-motion: reduce)' }));
  });
  afterEach(() => spy?.mockRestore());
}

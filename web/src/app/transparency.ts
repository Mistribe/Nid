import { useCallback, useState } from 'react';

export const TRANSPARENCY_KEY = 'nid.transparency';

const stored = () => {
  try {
    return localStorage.getItem(TRANSPARENCY_KEY) === 'reduce';
  } catch {
    return false;
  }
};

/** « Réduire la transparence »: a device choice (not a saved preference) that makes the glass surfaces opaque via `<html data-reduce-transparency>`. */
export function useReduceTransparency() {
  const [reduced, setReducedState] = useState(stored);
  const setReduced = useCallback((next: boolean) => {
    setReducedState(next);
    document.documentElement.toggleAttribute('data-reduce-transparency', next);
    try {
      if (next) localStorage.setItem(TRANSPARENCY_KEY, 'reduce');
      else localStorage.removeItem(TRANSPARENCY_KEY);
    } catch {
      // Storage refused (private browsing): the choice lasts until the page reloads.
    }
  }, []);
  return { reduced, setReduced };
}

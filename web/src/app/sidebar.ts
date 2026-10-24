import { useCallback, useState, useSyncExternalStore } from 'react';

const KEY = 'nid.sidebar';
// Between a tablet and a laptop the sidebar starts as a rail: its 14rem go to the page (design-system.md §6.1).
const NARROW = '(min-width: 48rem) and (max-width: 79.98rem)';

type Choice = 'rail' | 'full';

function stored(): Choice | null {
  try {
    const value = localStorage.getItem(KEY);
    return value === 'rail' || value === 'full' ? value : null;
  } catch {
    return null;
  }
}

const subscribe = (change: () => void) => {
  const media = window.matchMedia(NARROW);
  media.addEventListener('change', change);
  return () => media.removeEventListener('change', change);
};

/** The sidebar's state: the person's choice on this device, else a rail on a narrow screen. Ctrl/⌘ B or the top bar's button toggles it. */
export function useSidebar() {
  const [choice, setChoice] = useState(stored);
  const narrow = useSyncExternalStore(subscribe, () => window.matchMedia(NARROW).matches);
  const rail = choice ? choice === 'rail' : narrow;
  const toggle = useCallback(() => {
    const next: Choice = rail ? 'full' : 'rail';
    setChoice(next);
    try {
      localStorage.setItem(KEY, next);
    } catch {
      // Storage refused (private browsing): the choice lasts until the page reloads.
    }
  }, [rail]);
  return { rail, toggle };
}

/** « Alice Dev » → « AD »: the rail's stand-in for the signed-in person's name. */
export const initials = (name: string) => name.split(/\s+/).filter(Boolean).slice(0, 2).map((word) => word[0]!.toUpperCase()).join('');

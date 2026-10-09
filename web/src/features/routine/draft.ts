import type { RoutineValues } from './RoutineForm';

const key = (on: string) => `routine:${on}`;

const valid = (v: unknown): RoutineValues | null => {
  const lines = (v as { lines?: unknown } | null)?.lines;
  const ok = Array.isArray(lines) && lines.every((l: Record<string, unknown> | null) => l && typeof l === 'object'
    && typeof l.id === 'string' && typeof l.value === 'string' && typeof l.contribution === 'string' && typeof l.skip === 'boolean');
  return ok ? (v as RoutineValues) : null;
};

/** screens.md §5: the draft lives in the tab only; storage may be refused (private mode) and the page works without it. */
export function loadDraft(on: string): RoutineValues | null {
  try {
    const raw = sessionStorage.getItem(key(on));
    return raw ? valid(JSON.parse(raw)) : null;
  } catch {
    return null;
  }
}

export function saveDraft(on: string, values: RoutineValues): void {
  try { sessionStorage.setItem(key(on), JSON.stringify(values)); } catch { /* the draft is a convenience */ }
}

export function clearDraft(on: string): void {
  try { sessionStorage.removeItem(key(on)); } catch { /* nothing to clear */ }
}

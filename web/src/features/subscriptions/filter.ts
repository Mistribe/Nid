import type { components } from '@/api/schema';
import type { Summary } from './api';

export type Label = components['schemas']['LabelView'];

const fold = (text: string) => text.normalize('NFD').replace(/\p{Diacritic}/gu, '').toLowerCase();

/**
 * 1E2a design §3 and 1E2b2 design §3: q, status and labels filter in the browser; scope is the server's. Dates compare
 * as ISO strings. A subscription's labels are its provider's: one without a provider passes no label.
 */
export function filterSubscriptions(
  items: Summary[], search: { q?: string; status?: 'active' | 'ended'; labels?: string[] }, today: string,
): Summary[] {
  const q = search.q ? fold(search.q.trim()) : '';
  const labels = search.labels ?? [];
  return items.filter((item) => {
    if (q && !fold(item.displayName).includes(q)) return false;
    if (labels.length > 0 && !item.provider?.labels.some((l) => labels.includes(l.key))) return false;
    const ended = item.expiresOn !== null && item.expiresOn !== undefined && item.expiresOn < today;
    if (search.status === 'ended') return ended;
    if (search.status === 'active') return !ended;
    return true;
  });
}

/** `?label=a,b` (1E2b2 design §3.3): a label key never holds a comma. Blanks and duplicates are dropped. */
export function labelsOf(value: string | undefined): string[] {
  return [...new Set((value ?? '').split(',').map((key) => key.trim()).filter((key) => key !== ''))];
}

export function labelParam(keys: string[]): string | undefined {
  return keys.length > 0 ? keys.join(',') : undefined;
}

/** The labels the listed subscriptions' providers carry, once each, by name: a label nothing carries is never offered. */
export function labelsInUse(items: Summary[]): Label[] {
  const byKey = new Map<string, Label>();
  for (const item of items) for (const label of item.provider?.labels ?? []) byKey.set(label.key, label);
  return [...byKey.values()].sort((a, b) => a.name.localeCompare(b.name, 'fr'));
}

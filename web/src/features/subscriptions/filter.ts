import type { Summary } from './api';

const fold = (text: string) => text.normalize('NFD').replace(/\p{Diacritic}/gu, '').toLowerCase();

/** 1E2a design §3: q and status filter in the browser; scope is the server's. Dates compare as ISO strings. */
export function filterSubscriptions(items: Summary[], search: { q?: string; status?: 'active' | 'ended' }, today: string): Summary[] {
  const q = search.q ? fold(search.q.trim()) : '';
  return items.filter((item) => {
    if (q && !fold(item.displayName).includes(q)) return false;
    const ended = item.expiresOn !== null && item.expiresOn !== undefined && item.expiresOn < today;
    if (search.status === 'ended') return ended;
    if (search.status === 'active') return !ended;
    return true;
  });
}

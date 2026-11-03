import type { ProjectionHorizon } from '@/app/router';

/** Spec §14.2: the compared scenarios (comma-separated, in order) and the horizon; absent is 10 years. */
export type CompareSearch = { ids?: string; horizon?: ProjectionHorizon };

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
/** The server's limit (CompareScenariosHandler.MaxScenarios). */
export const MAX_COMPARED = 3;

/** The URL as the page reads it: at most three distinct well-formed ids, a horizon the dashboard's card offers too. */
export function compareSearch(search: Record<string, unknown>): CompareSearch {
  const ids = typeof search.ids === 'string'
    ? [...new Set(search.ids.split(',').filter((part) => UUID.test(part)))].slice(0, MAX_COMPARED)
    : [];
  const horizon = search.horizon;
  const kept = horizon === '5y' || horizon === '20y' || horizon === '30y'
    || (typeof horizon === 'string' && horizon.startsWith('retire:') && UUID.test(horizon.slice('retire:'.length)));
  return { ...(ids.length > 0 ? { ids: ids.join(',') } : {}), ...(kept ? { horizon: horizon as ProjectionHorizon } : {}) };
}

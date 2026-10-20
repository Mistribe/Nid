import { keepPreviousData } from '@tanstack/react-query';
import { useSearch } from '@tanstack/react-router';
import { useApi } from '@/api/ApiProvider';
import type { IncomeSearch, RootSearch } from '@/app/router';
import { OVERVIEW, type IncomeOverview } from '../api';

/** The overview the URL asks for; the previous answer stays up while a filter reloads, so the controls keep their focus. */
export function useOverview(over: { ended?: boolean; enabled?: boolean } = {}): { data?: IncomeOverview; error: unknown } {
  const { $api } = useApi();
  const search: RootSearch & IncomeSearch = useSearch({ strict: false });
  const query = {
    scope: search.scope === 'me' ? 'me' : 'household',
    period: search.period ?? 'year',
    ...(search.person ? { person: search.person } : {}),
    ...(search.kind ? { kind: search.kind } : {}),
    ...(search.source ? { source: search.source } : {}),
    ...((over.ended ?? search.ended === 1) ? { ended: true } : {}),
  };
  const { data, error } = $api.useQuery('get', OVERVIEW, { params: { query } }, { placeholderData: keepPreviousData, enabled: over.enabled ?? true });
  return { data, error };
}

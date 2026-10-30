import { useApi } from '@/api/ApiProvider';
import { LOANS } from '../../loans/api';
import { PROPERTIES } from '../../properties/api';
import { SCENARIOS } from '../api';

/** The targets an event may name, each list read only by the kind that needs it; the event list names its targets from them too. */
export function usePickOptions(list: 'loans' | 'properties' | 'purchases' | null, scope: 'me' | 'household') {
  const { $api } = useApi();
  const query = { params: { query: { scope } } };
  const loans = $api.useQuery('get', LOANS, query, { enabled: list === 'loans' });
  const properties = $api.useQuery('get', PROPERTIES, query, { enabled: list === 'properties' });
  const purchases = $api.useQuery('get', SCENARIOS, query, { enabled: list === 'purchases' });
  if (list === 'loans') return (loans.data?.items ?? []).filter((l) => !l.ended).map((l) => ({ value: l.id, label: l.name }));
  if (list === 'properties') return (properties.data?.items ?? []).map((p) => ({ value: p.id, label: p.name }));
  if (list === 'purchases') return (purchases.data?.items ?? []).filter((s) => s.kind === 'property_purchase').map((s) => ({ value: s.id, label: s.name }));
  return [];
}

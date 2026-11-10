import { useApi } from './ApiProvider';
import type { components } from './schema';

export type ChildcareSummary = components['schemas']['ChildcareSummary'];
export type ChildcareList = components['schemas']['ChildcareList'];

export const CHILDCARE = '/api/v1/childcare' as const;

export type Scope = 'me' | 'household';

/** `closed` lists the open arrangements and the closed ones; the page keeps the rows it needs. */
export function useChildcareList(scope: Scope, closed: boolean) {
  const { $api } = useApi();
  return $api.useQuery('get', CHILDCARE, { params: { query: { scope, closed } } });
}

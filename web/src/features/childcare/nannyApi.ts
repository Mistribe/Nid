import { useQueryClient, type QueryClient } from '@tanstack/react-query';
import { useApi } from '@/api/ApiProvider';
import type { components } from '@/api/schema';
import type { Scope } from '@/api/childcare';

export type NannySummary = components['schemas']['NannySummary'];
export type NannyDetail = components['schemas']['NannyDetail'];
export type NannyMonth = components['schemas']['NannyMonthView'];

export const NANNIES = '/api/v1/childcare/nannies' as const;
export const NANNY = '/api/v1/childcare/nannies/{id}' as const;
export const NANNY_MONTH = '/api/v1/childcare/nannies/{id}/months/{month}' as const;
const TAX = '/api/v1/tax' as const;

/** #94: every write invalidates the list, the nanny and the tax estimate that counts its months. */
export function invalidateNannies(queryClient: QueryClient): Promise<void> {
  return Promise.all([NANNIES, NANNY, TAX].map((path) => queryClient.invalidateQueries({ queryKey: ['get', path] }))).then(() => undefined);
}

export function useNannies(scope: Scope, closed: boolean) {
  const { $api } = useApi();
  return $api.useQuery('get', NANNIES, { params: { query: { scope, closed } } });
}

export function useNanny(id: string, year?: number) {
  const { $api } = useApi();
  return $api.useQuery('get', NANNY, { params: { path: { id }, query: year ? { year } : {} } });
}

function useWrite() {
  const queryClient = useQueryClient();
  return { onSuccess: () => invalidateNannies(queryClient) };
}

export const useCreateNanny = () => useApi().$api.useMutation('post', NANNIES, useWrite());
export const usePutNannyMonth = () => useApi().$api.useMutation('put', NANNY_MONTH, useWrite());
export const useDeleteNannyMonth = () => useApi().$api.useMutation('delete', NANNY_MONTH, useWrite());
export const useCloseNanny = () => useApi().$api.useMutation('post', '/api/v1/childcare/nannies/{id}/close', useWrite());
export const useReopenNanny = () => useApi().$api.useMutation('post', '/api/v1/childcare/nannies/{id}/reopen', useWrite());

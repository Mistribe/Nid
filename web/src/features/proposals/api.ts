import type { QueryClient } from '@tanstack/react-query';
import { useApi } from '@/api/ApiProvider';
import type { components } from '@/api/schema';

export const PROPOSALS = '/api/v1/core/proposals';
export const PROPOSAL = '/api/v1/core/proposals/{id}';
export const APPROVE = '/api/v1/core/proposals/{id}/approve';
export const REJECT = '/api/v1/core/proposals/{id}/reject';

export type ProposalView = components['schemas']['ProposalView'];
export type ProposalDetail = components['schemas']['ProposalDetail'];

/** Every proposal query: the list's tabs, the badge, a detail. */
export const invalidateProposals = (queryClient: QueryClient) =>
  queryClient.invalidateQueries({ predicate: (q) => typeof q.queryKey[1] === 'string' && q.queryKey[1].startsWith(PROPOSALS) });

/** Precision 1: the pending list, shared by the « En attente » tab, the badge and the two lines. */
export function usePending(): ProposalView[] | undefined {
  const { $api } = useApi();
  return $api.useQuery('get', PROPOSALS, { params: { query: { status: 'pending' } } }).data?.items;
}

export function usePendingCount(): number | undefined {
  return usePending()?.length;
}

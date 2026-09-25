import type { QueryClient } from '@tanstack/react-query';
import type { components } from '@/api/schema';

export type Summary = components['schemas']['SubscriptionSummary'];
export type ListResponse = components['schemas']['SubscriptionList'];
export type Detail = components['schemas']['SubscriptionDetail'];
export type Phase = components['schemas']['PhaseView'];
export type Purchase = components['schemas']['PurchaseView'];
export type Coverage = components['schemas']['CoverageView'];

export const LIST = '/api/v1/subscriptions' as const;
export const DETAIL = '/api/v1/subscriptions/{id}' as const;
export const PROVIDERS = '/api/v1/subscriptions/providers' as const;
export const PERSONS = '/api/v1/core/persons' as const;

/** Every write invalidates the list and every detail (1E2a design §4): pessimistic, then targeted. */
export function invalidateSubscriptions(queryClient: QueryClient): Promise<void> {
  return Promise.all([
    queryClient.invalidateQueries({ queryKey: ['get', LIST] }),
    queryClient.invalidateQueries({ queryKey: ['get', DETAIL] }),
  ]).then(() => undefined);
}

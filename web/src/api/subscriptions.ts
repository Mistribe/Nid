import type { QueryClient } from '@tanstack/react-query';
import { DASHBOARD } from './dashboard';
import type { components } from '@/api/schema';

export type Summary = components['schemas']['SubscriptionSummary'];
export type ListResponse = components['schemas']['SubscriptionList'];
export type Detail = components['schemas']['SubscriptionDetail'];
export type Phase = components['schemas']['PhaseView'];
export type Purchase = components['schemas']['PurchaseView'];
export type Coverage = components['schemas']['CoverageView'];
export type Overview = components['schemas']['SubscriptionsOverview'];
export type MonthView = Overview['months'][number];
export type Charges = components['schemas']['SubscriptionCharges'];

/**
 * What a PATCH sends: the schema lists every `clear…` flag as required, but the server defaults a missing one to false, so a
 * form sends the flags of the fields it edits.
 */
export type EditBody = Partial<components['schemas']['EditSubscriptionBody']>;
export const asEditBody = (body: EditBody) => body as components['schemas']['EditSubscriptionBody'];

export const LIST = '/api/v1/subscriptions' as const;
export const OVERVIEW = '/api/v1/subscriptions/overview' as const;
export const CHARGES = '/api/v1/subscriptions/charges' as const;
export const DETAIL = '/api/v1/subscriptions/{id}' as const;
export const INSURANCE = '/api/v1/subscriptions/{id}/insurance' as const;
export const PROVIDERS = '/api/v1/subscriptions/providers' as const;
export const LABELS = '/api/v1/subscriptions/labels' as const;

/** Every write invalidates the list, the overview, the calendar and every detail (1E2a design §4): pessimistic, then targeted. */
export function invalidateSubscriptions(queryClient: QueryClient): Promise<void> {
  return Promise.all([
    queryClient.invalidateQueries({ queryKey: ['get', LIST] }),
    queryClient.invalidateQueries({ queryKey: ['get', OVERVIEW] }),
    queryClient.invalidateQueries({ queryKey: ['get', CHARGES] }),
    queryClient.invalidateQueries({ queryKey: ['get', DASHBOARD] }),
    queryClient.invalidateQueries({ queryKey: ['get', DETAIL] }),
  ]).then(() => undefined);
}

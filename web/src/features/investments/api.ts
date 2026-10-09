import type { QueryClient } from '@tanstack/react-query';
import { DASHBOARD, NET_WORTH } from '../dashboard/api';
import type { components } from '@/api/schema';

export type EnvelopeSummary = components['schemas']['EnvelopeSummary'];
export type EnvelopeList = components['schemas']['EnvelopeList'];
export type EnvelopeDetail = components['schemas']['EnvelopeDetail'];
export type Figures = components['schemas']['FiguresView'];
export type Valuation = components['schemas']['ValuationView'];
export type Flow = components['schemas']['FlowView'];

export const ENVELOPES = '/api/v1/investments/envelopes' as const;
export const ENVELOPE = '/api/v1/investments/envelopes/{id}' as const;
export const VALUATIONS = '/api/v1/investments/envelopes/{id}/valuations' as const;
export const VALUATION = '/api/v1/investments/envelopes/{id}/valuations/{valuationId}' as const;
export const SERIES = '/api/v1/investments/envelopes/{id}/series' as const;
export const PERFORMANCE = '/api/v1/investments/envelopes/{id}/performance' as const;
export const FLOWS = '/api/v1/investments/envelopes/{id}/flows' as const;
export const TRANSFERS = '/api/v1/investments/transfers' as const;
export const CLOSE = '/api/v1/investments/envelopes/{id}/close' as const;
export const REOPEN = '/api/v1/investments/envelopes/{id}/reopen' as const;
export const FLOW = '/api/v1/investments/envelopes/{id}/flows/{flowId}' as const;

/** Every write invalidates the list, every detail, every value series and every period, as for subscriptions. */
export function invalidateInvestments(queryClient: QueryClient): Promise<void> {
  return Promise.all([
    queryClient.invalidateQueries({ queryKey: ['get', ENVELOPES] }),
    queryClient.invalidateQueries({ queryKey: ['get', DASHBOARD] }),
    queryClient.invalidateQueries({ queryKey: ['get', NET_WORTH] }),
    queryClient.invalidateQueries({ queryKey: ['get', ENVELOPE] }),
    queryClient.invalidateQueries({ queryKey: ['get', SERIES] }),
    queryClient.invalidateQueries({ queryKey: ['get', PERFORMANCE] }),
  ]).then(() => undefined);
}

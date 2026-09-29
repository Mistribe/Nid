import type { QueryClient } from '@tanstack/react-query';
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
export const FLOWS = '/api/v1/investments/envelopes/{id}/flows' as const;
export const FLOW = '/api/v1/investments/envelopes/{id}/flows/{flowId}' as const;

/** Every write invalidates the list and every detail, as for subscriptions. */
export function invalidateInvestments(queryClient: QueryClient): Promise<void> {
  return Promise.all([
    queryClient.invalidateQueries({ queryKey: ['get', ENVELOPES] }),
    queryClient.invalidateQueries({ queryKey: ['get', ENVELOPE] }),
  ]).then(() => undefined);
}

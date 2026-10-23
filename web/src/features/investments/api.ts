import type { QueryClient } from '@tanstack/react-query';
import { DASHBOARD, NET_WORTH } from '../dashboard/api';
import { TAX_SALE } from '../tax/api';
import type { components } from '@/api/schema';

export type EnvelopeSummary = components['schemas']['EnvelopeSummary'];
export type EnvelopeList = components['schemas']['EnvelopeList'];
export type EnvelopeDetail = components['schemas']['EnvelopeDetail'];
export type Figures = components['schemas']['FiguresView'];
export type Valuation = components['schemas']['ValuationView'];
export type Flow = components['schemas']['FlowView'];
export type HoldingsView = components['schemas']['HoldingsView'];
export type Holding = components['schemas']['HoldingView'];
export type HoldingBody = components['schemas']['HoldingBody'];
export type HoldingWarning = components['schemas']['HoldingWarningView'];
export type Movement = components['schemas']['MovementView'];
export type CostReport = components['schemas']['CostReportView'];
export type Snapshot = components['schemas']['SnapshotView'];
export type InvestmentsOverview = components['schemas']['InvestmentsOverview'];
export type EnvelopeOverviewRow = components['schemas']['EnvelopeOverviewRow'];
export type OverviewPoint = components['schemas']['OverviewPointView'];
export type Watch = components['schemas']['EnvelopeWatchView'];
export type HouseholdPerformance = components['schemas']['InvestmentsPerformance'];
export type EnvelopePeriodRow = components['schemas']['EnvelopePeriodRow'];
export type HouseholdHoldings = components['schemas']['InvestmentsHoldings'];
export type HoldingOverviewRow = components['schemas']['HoldingOverviewRow'];

export const ENVELOPES = '/api/v1/investments/envelopes' as const;
export const OVERVIEW = '/api/v1/investments/overview' as const;
export const HOUSEHOLD_PERFORMANCE = '/api/v1/investments/overview/performance' as const;
export const HOUSEHOLD_HOLDINGS = '/api/v1/investments/overview/holdings' as const;
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
export const HOLDINGS = '/api/v1/investments/envelopes/{id}/holdings' as const;
export const HOLDING = '/api/v1/investments/envelopes/{id}/holdings/{holdingId}' as const;
export const PRICE_ACCEPT = '/api/v1/investments/envelopes/{id}/holdings/{holdingId}/price/accept' as const;
export const MOVEMENTS = '/api/v1/investments/envelopes/{id}/holdings/{holdingId}/movements' as const;
export const HOLDING_SERIES = '/api/v1/investments/envelopes/{id}/holdings/{holdingId}/series' as const;
export const COST_REPORTS = '/api/v1/investments/envelopes/{id}/holdings/{holdingId}/cost-reports' as const;
export const COST_REPORT = '/api/v1/investments/envelopes/{id}/holdings/{holdingId}/cost-reports/{year}' as const;
export const SNAPSHOTS = '/api/v1/investments/envelopes/{id}/holding-snapshots' as const;
export const SWITCHES = '/api/v1/investments/envelopes/{id}/switches' as const;

/** Every write invalidates the list, the overview, every detail, every value series and every period, as for subscriptions. */
export function invalidateInvestments(queryClient: QueryClient): Promise<void> {
  return Promise.all([
    queryClient.invalidateQueries({ queryKey: ['get', ENVELOPES] }),
    queryClient.invalidateQueries({ queryKey: ['get', OVERVIEW] }),
    queryClient.invalidateQueries({ queryKey: ['get', HOUSEHOLD_PERFORMANCE] }),
    queryClient.invalidateQueries({ queryKey: ['get', HOUSEHOLD_HOLDINGS] }),
    queryClient.invalidateQueries({ queryKey: ['get', DASHBOARD] }),
    queryClient.invalidateQueries({ queryKey: ['get', NET_WORTH] }),
    queryClient.invalidateQueries({ queryKey: ['get', TAX_SALE] }),
    queryClient.invalidateQueries({ queryKey: ['get', ENVELOPE] }),
    queryClient.invalidateQueries({ queryKey: ['get', SERIES] }),
    queryClient.invalidateQueries({ queryKey: ['get', PERFORMANCE] }),
    // A goal's figures are its envelopes' values and flows (paths inline: goals/api stays out of the initial chunk).
    ...[HOLDINGS, MOVEMENTS, HOLDING_SERIES, COST_REPORTS, SNAPSHOTS, '/api/v1/goals', '/api/v1/goals/{id}'].map((path) => queryClient.invalidateQueries({ queryKey: ['get', path] })),
  ]).then(() => undefined);
}

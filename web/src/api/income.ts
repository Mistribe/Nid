import type { QueryClient } from '@tanstack/react-query';
import { DASHBOARD } from './dashboard';
import { invalidateDocuments } from './documents';
import type { components } from '@/api/schema';

export type IncomeSourceSummary = components['schemas']['IncomeSourceSummary'];
export type IncomeSourceList = components['schemas']['IncomeSourceList'];
export type IncomeSourceDetail = components['schemas']['IncomeSourceDetail'];
export type IncomePhase = components['schemas']['IncomePhaseView'];
export type IncomeGap = components['schemas']['IncomeGapView'];
export type IncomeExpected = components['schemas']['IncomeExpected'];
export type IncomeOverview = components['schemas']['IncomeOverview'];
export type IncomeOverviewShare = components['schemas']['IncomeOverviewShare'];
export type IncomeOverviewFigures = components['schemas']['IncomeOverviewFigures'];
export type IncomeOneOff = components['schemas']['IncomeOneOffView'];
export type IncomeSourceOverview = components['schemas']['IncomeSourceOverview'];
export type IncomeChange = components['schemas']['IncomeChangeView'];

export const OVERVIEW = '/api/v1/income/overview' as const;
export const SOURCES = '/api/v1/income/sources' as const;
export const SOURCE = '/api/v1/income/sources/{id}' as const;
export const SOURCE_OVERVIEW = '/api/v1/income/sources/{id}/overview' as const;
export const PHASES = '/api/v1/income/sources/{id}/phases' as const;
export const EXPECTED = '/api/v1/income/sources/{id}/expected' as const;
export const PHASE_CHANGES = '/api/v1/income/sources/{id}/phase-changes' as const;
export const ONE_OFFS = '/api/v1/income/sources/{id}/one-offs' as const;
export const ONE_OFF = '/api/v1/income/sources/{id}/one-offs/{flowId}' as const;
export const PHASE = '/api/v1/income/sources/{id}/phases/{phaseId}' as const;

/** Every write invalidates the list, every detail, every source overview, every expected income and the documents (a correction or a deletion moves or drops their links). */
export function invalidateIncome(queryClient: QueryClient): Promise<void> {
  return Promise.all([
    queryClient.invalidateQueries({ queryKey: ['get', SOURCES] }),
    queryClient.invalidateQueries({ queryKey: ['get', OVERVIEW] }),
    queryClient.invalidateQueries({ queryKey: ['get', DASHBOARD] }),
    queryClient.invalidateQueries({ queryKey: ['get', SOURCE] }),
    queryClient.invalidateQueries({ queryKey: ['get', SOURCE_OVERVIEW] }),
    queryClient.invalidateQueries({ queryKey: ['get', EXPECTED] }),
    invalidateDocuments(queryClient),
  ]).then(() => undefined);
}
export const CLOSE_SOURCE = '/api/v1/income/sources/{id}/close' as const;
export const REOPEN_SOURCE = '/api/v1/income/sources/{id}/reopen' as const;

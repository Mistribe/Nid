import type { QueryClient } from '@tanstack/react-query';
import { DASHBOARD } from '../dashboard/api';
import type { components } from '@/api/schema';

export type IncomeSourceSummary = components['schemas']['IncomeSourceSummary'];
export type IncomeSourceList = components['schemas']['IncomeSourceList'];
export type IncomeSourceDetail = components['schemas']['IncomeSourceDetail'];
export type IncomePhase = components['schemas']['IncomePhaseView'];
export type IncomeGap = components['schemas']['IncomeGapView'];
export type IncomeExpected = components['schemas']['IncomeExpected'];
export type IncomeOneOff = components['schemas']['IncomeOneOffView'];

export const SOURCES = '/api/v1/income/sources' as const;
export const SOURCE = '/api/v1/income/sources/{id}' as const;
export const PHASES = '/api/v1/income/sources/{id}/phases' as const;
export const EXPECTED = '/api/v1/income/sources/{id}/expected' as const;
export const PHASE_CHANGES = '/api/v1/income/sources/{id}/phase-changes' as const;
export const ONE_OFFS = '/api/v1/income/sources/{id}/one-offs' as const;
export const ONE_OFF = '/api/v1/income/sources/{id}/one-offs/{flowId}' as const;
export const PHASE = '/api/v1/income/sources/{id}/phases/{phaseId}' as const;

/** Every write invalidates the list, every detail and every expected income. */
export function invalidateIncome(queryClient: QueryClient): Promise<void> {
  return Promise.all([
    queryClient.invalidateQueries({ queryKey: ['get', SOURCES] }),
    queryClient.invalidateQueries({ queryKey: ['get', DASHBOARD] }),
    queryClient.invalidateQueries({ queryKey: ['get', SOURCE] }),
    queryClient.invalidateQueries({ queryKey: ['get', EXPECTED] }),
  ]).then(() => undefined);
}
export const CLOSE_SOURCE = '/api/v1/income/sources/{id}/close' as const;
export const REOPEN_SOURCE = '/api/v1/income/sources/{id}/reopen' as const;

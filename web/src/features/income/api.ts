import type { QueryClient } from '@tanstack/react-query';
import type { components } from '@/api/schema';

export type IncomeSourceSummary = components['schemas']['IncomeSourceSummary'];
export type IncomeSourceList = components['schemas']['IncomeSourceList'];
export type IncomeSourceDetail = components['schemas']['IncomeSourceDetail'];
export type IncomePhase = components['schemas']['IncomePhaseView'];
export type IncomeGap = components['schemas']['IncomeGapView'];

export const SOURCES = '/api/v1/income/sources' as const;
export const SOURCE = '/api/v1/income/sources/{id}' as const;
export const PHASES = '/api/v1/income/sources/{id}/phases' as const;
export const PHASE = '/api/v1/income/sources/{id}/phases/{phaseId}' as const;

/** Every write invalidates the list and every detail (2C2a design §7). */
export function invalidateIncome(queryClient: QueryClient): Promise<void> {
  return Promise.all([
    queryClient.invalidateQueries({ queryKey: ['get', SOURCES] }),
    queryClient.invalidateQueries({ queryKey: ['get', SOURCE] }),
  ]).then(() => undefined);
}

import type { QueryClient } from '@tanstack/react-query';
import type { components } from '@/api/schema';

export type LoanSummary = components['schemas']['LoanSummary'];
export type LoanList = components['schemas']['LoanList'];
export type LoanDetail = components['schemas']['LoanDetail'];
export type LoanSchedule = components['schemas']['LoanScheduleView'];
export type LoanScheduleRow = components['schemas']['LoanScheduleRowView'];
export type LoanScheduleYear = components['schemas']['LoanScheduleYearView'];
export type LoanWarning = components['schemas']['LoanWarningView'];

export const LOANS = '/api/v1/realestate/loans' as const;
export const LOAN = '/api/v1/realestate/loans/{id}' as const;
export const SCHEDULE = '/api/v1/realestate/loans/{id}/schedule' as const;
export const CONTRACT = '/api/v1/realestate/loans/{id}/contract' as const;

/** Every write invalidates the list, every detail and every schedule (3B1 design §7). */
export function invalidateLoans(queryClient: QueryClient): Promise<void> {
  return Promise.all([LOANS, LOAN, SCHEDULE].map((path) => queryClient.invalidateQueries({ queryKey: ['get', path] }))).then(() => undefined);
}

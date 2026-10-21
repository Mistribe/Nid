import type { QueryClient } from '@tanstack/react-query';
import type { components } from '@/api/schema';

export type LoanSummary = components['schemas']['LoanSummary'];
export type LoanList = components['schemas']['LoanList'];
export type LoanDetail = components['schemas']['LoanDetail'];
export type LoanSchedule = components['schemas']['LoanScheduleView'];
export type LoanScheduleRow = components['schemas']['LoanScheduleRowView'];
export type LoanScheduleYear = components['schemas']['LoanScheduleYearView'];
export type LoanWarning = components['schemas']['LoanWarningView'];
export type LoanFee = components['schemas']['LoanFeeView'];
export type LoanRepayment = components['schemas']['LoanRepaymentView'];
export type LoanSimulation = components['schemas']['LoanSimulationView'];
export type LoanOutcome = components['schemas']['LoanOutcomeView'];
export type BankRow = components['schemas']['BankRowView'];
export type LoanComparison = components['schemas']['ScheduleComparisonView'];
export type BankPreview = components['schemas']['BankSchedulePreview'];
export type LoansOverview = components['schemas']['LoansOverview'];
export type LoanOverviewRow = components['schemas']['LoanOverviewRow'];
export type BalanceMonth = components['schemas']['BalanceMonthView'];
export type LoanEvent = components['schemas']['LoanEventView'];

export const LOANS = '/api/v1/realestate/loans' as const;
export const OVERVIEW = '/api/v1/realestate/loans/overview' as const;
export const LOAN = '/api/v1/realestate/loans/{id}' as const;
export const SCHEDULE = '/api/v1/realestate/loans/{id}/schedule' as const;
export const CONTRACT = '/api/v1/realestate/loans/{id}/contract' as const;
export const FEES = '/api/v1/realestate/loans/{id}/fees' as const;
export const FEE = '/api/v1/realestate/loans/{id}/fees/{feeId}' as const;
export const REPAYMENTS = '/api/v1/realestate/loans/{id}/repayments' as const;
export const REPAYMENT = '/api/v1/realestate/loans/{id}/repayments/{eventId}' as const;
export const SIMULATIONS = '/api/v1/realestate/loans/{id}/simulations' as const;
export const BANK_SCHEDULE = '/api/v1/realestate/loans/{id}/bank-schedule' as const;
export const BANK_PREVIEW = '/api/v1/realestate/loans/{id}/bank-schedule/preview' as const;

/** Every write invalidates the list, the overview, every detail and every schedule (3B1 design §7). */
export function invalidateLoans(queryClient: QueryClient): Promise<void> {
  return Promise.all([LOANS, OVERVIEW, LOAN, SCHEDULE].map((path) => queryClient.invalidateQueries({ queryKey: ['get', path] }))).then(() => undefined);
}

import type { LoanDetail, LoanList, LoanSchedule, LoanScheduleRow, LoanScheduleYear, LoanSimulation, LoanSummary } from './api';

export const LOAN = '0192f0aa-0000-7000-8000-00000000d001';

/**
 * loans.md §4's golden case — 200 000 € at 3,5 % over 240 payments from 2025-11-15 — read on 2026-10-12, after its
 * eleventh payment: the figures are the engine's.
 */
export const summary = (over: Partial<LoanSummary> = {}): LoanSummary => ({
  id: LOAN, name: 'Prêt maison', lender: 'Banque', currency: 'EUR', visibility: 'private', principal: '200000.00',
  remainingBalance: '193564.23', nextPaymentOn: '2026-10-15', nextPaymentTotal: '1159.92', currentRate: '0.035',
  endDate: '2045-10-15', canWrite: true, ended: false, ...over,
});

export const list = (items: LoanSummary[]): LoanList => ({ items });

export const loanDetail = (over: Partial<LoanDetail> = {}): LoanDetail => ({
  id: LOAN, name: 'Prêt maison', lender: 'Banque', note: null, currency: 'EUR', visibility: 'private',
  borrowers: [{ personId: '01926b9e-0000-7000-8000-00000000000a', shareBp: 10000 }],
  contract: {
    principal: '200000.00', disbursedOn: '2025-10-15', firstPaymentDate: '2025-11-15', totalPayments: 240, rounding: 'half_up',
    firstPeriod: 'full', version: 3, insurances: [],
    phases: [{ seq: 1, mode: 'annuity', annualRate: '0.035', rateType: 'fixed', payments: null, paymentOverride: null, openingBalanceOverride: null, note: null }],
  },
  fees: [], repayments: [],
  figures: {
    balance: '193564.23', principalRepaid: '6435.77', interestPaid: '6323.35', insurancePaid: '0.00', feesPaid: '0.00',
    currentPhaseSeq: 1, currentRate: '0.035', paymentsLeft: 229, nextPaymentOn: '2026-10-15', nextPaymentTotal: '1159.92',
    endDate: '2045-10-15', totalCost: '78380.66', costPaid: '6323.35', costRemaining: '72057.31',
  },
  warnings: [], canWrite: true, ended: false, scheduleSource: 'computed', bankSchedule: null, comparison: null, ...over,
});

/** `count` monthly rows from `first` (a 15th), every figure alike: the tests read dates, numbers and marks, not sums. */
export const rows = (first: string, count: number): LoanScheduleRow[] => {
  const [y, m] = first.split('-').map(Number) as [number, number];
  return Array.from({ length: count }, (_, i) => {
    const month = m - 1 + i;
    return {
      number: i + 1, dueDate: `${y + Math.floor(month / 12)}-${String((month % 12) + 1).padStart(2, '0')}-15`, phaseSeq: 1,
      annualRate: '0.035', payment: '1159.92', interest: '500.00', principal: '659.92', insurance: '0.00', balance: '150000.00',
      earlyRepayment: null, fromBank: false,
    };
  });
};

export const year = (over: Partial<LoanScheduleYear> & { year: number }): LoanScheduleYear => ({
  payment: '13919.04', interest: '6400.81', principal: '7518.23', insurance: '0.00', earlyRepayment: '0.00', balance: '178785.99', ...over,
});

/** November 2025 to October 2027: two payments in 2025, twelve in 2026, ten in 2027. */
export const schedule = (over: Partial<LoanSchedule> = {}): LoanSchedule => ({
  rows: rows('2025-11-15', 24),
  years: [
    year({ year: 2025, payment: '2319.84', interest: '1164.98', principal: '1154.86', balance: '198845.14' }),
    year({ year: 2026 }),
    year({ year: 2027, payment: '11599.20', balance: '171000.35' }),
  ],
  totalInterest: '78380.66', totalPrincipal: '200000.00', warnings: [], ...over,
});

/** 10 000 € repaid on 2026-10-12 with a 300 € penalty, both effects as the server compares them. */
export const simulation = (over: Partial<LoanSimulation> = {}): LoanSimulation => ({
  on: '2026-10-12', amount: '10000.00', penalty: '300.00',
  outcomes: [
    {
      effect: 'reduce_term', paymentBefore: '1159.92', paymentAfter: '1159.92', endBefore: '2045-10-15', endAfter: '2044-07-15',
      paymentsBefore: 240, paymentsAfter: 226, interestBefore: '78380.66', interestAfter: '71814.08', interestSaved: '6566.58', netSaving: '6266.58',
    },
    {
      effect: 'reduce_payment', paymentBefore: '1159.92', paymentAfter: '1088.43', endBefore: '2045-10-15', endAfter: '2045-10-15',
      paymentsBefore: 240, paymentsAfter: 240, interestBefore: '78380.66', interestAfter: '75050.00', interestSaved: '3330.66', netSaving: '3030.66',
    },
  ],
  ...over,
});

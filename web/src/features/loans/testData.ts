import type { BankRow, LoanComparison, LoanDetail, LoanList, LoanOverviewRow, LoansOverview, LoanSchedule, LoanScheduleRow, LoanScheduleYear, LoanSimulation, LoanSummary } from './api';

export const LOAN = '0192f0aa-0000-7000-8000-00000000d001';

/**
 * loans.md §4's golden case — 200 000 € at 3,5 % over 240 payments from 2025-11-15 — read on 2026-10-12, after its
 * eleventh payment: the figures are the engine's.
 */
export const summary = (over: Partial<LoanSummary> = {}): LoanSummary => ({
  id: LOAN, name: 'Prêt maison', lender: 'Banque', currency: 'EUR', visibility: 'private', principal: '200000.00',
  remainingBalance: '193564.23', nextPaymentOn: '2026-10-15', nextPaymentTotal: '1159.92', currentRate: '0.035',
  endDate: '2045-10-15', canWrite: true, ended: false, owners: [], ...over,
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

/** The golden case's first three payments as its bank writes them, dated as `rows` (3F2). */
export const bankRows = (): BankRow[] => [
  { dueDate: '2025-11-15', payment: '1159.92', interest: '583.33', principal: '576.59', insurance: null, crd: '199423.41' },
  { dueDate: '2025-12-15', payment: '1159.92', interest: '581.65', principal: '578.27', insurance: null, crd: '198845.14' },
  { dueDate: '2026-01-15', payment: '1159.92', interest: '579.96', principal: '579.96', insurance: null, crd: '198265.18' },
];

export const bankTable = () => ({ rows: bankRows(), firstDueOn: '2025-11-15', lastDueOn: '2026-01-15' });

/** A comparison that agrees within rounding, unless `over` says otherwise. */
export const agreeing = (over: Partial<LoanComparison> = {}): LoanComparison => ({
  maxGap: '0.00', maxGapOn: null, divergesOn: null, interestGap: '0.00', rowsBeyondRounding: 0, ...over,
});

/** The loan with its bank's table stored, agreeing with the calculation. */
export const withTable = (over: Partial<LoanDetail> = {}): LoanDetail => loanDetail({ bankSchedule: bankTable(), comparison: agreeing(), ...over });

const eur = (amount: string) => ({ amount, currency: 'EUR' });

/** A running loan of the overview: the golden case read on 2026-10-12, as `summary`. */
export const overviewRow = (over: Partial<LoanOverviewRow> = {}): LoanOverviewRow => ({
  id: LOAN, name: 'Prêt maison', lender: 'Banque', propertyId: null, propertyName: null, owners: [], currency: 'EUR',
  principal: '200000.00', balance: '193564.23', repaidShare: '0.0322', monthlyPayment: '1159.92', currentRate: '0.035',
  endDate: '2045-10-15', balanceConverted: eur('193564.23'), monthlyPaymentConverted: eur('1159.92'), ...over,
});

/** The household's overview: no loan unless `over` says otherwise (the figures of an empty household are zeros). */
export const overview = (over: Partial<LoansOverview> = {}): LoansOverview => ({
  currency: 'EUR', convertedIsEstimate: false, runningCount: 0, endedCount: 0, balance: eur('0.00'), principal: eur('0.00'),
  principalRepaid: eur('0.00'), repaidShare: null, monthlyPayment: eur('0.00'), costPaid: eur('0.00'), costRemaining: eur('0.00'),
  freeOn: null, nextPaymentOn: null, nextPaymentTotal: null, loans: [], months: [], events: [], spans: [], steps: [], byPerson: [], findings: [], ...over,
});

export const ALICE = '0192f0aa-0000-7000-8000-0000000000a1';
export const BOB = '0192f0aa-0000-7000-8000-0000000000b2';

/** Two running loans, the house (Alice and Bob) and the car (Alice), the car's last payment in January 2027. */
export const CAR = '0192f0aa-0000-7000-8000-00000000d003';
export const household = (over: Partial<LoansOverview> = {}): LoansOverview => overview({
  runningCount: 2, balance: eur('199564.23'), principal: eur('212000.00'), principalRepaid: eur('12435.77'), repaidShare: '0.0587',
  monthlyPayment: eur('2159.92'), costPaid: eur('6823.35'), costRemaining: eur('72157.31'), freeOn: '2045-10-15',
  nextPaymentOn: '2026-11-15', nextPaymentTotal: eur('2159.92'),
  loans: [
    overviewRow(),
    overviewRow({ id: CAR, name: 'Prêt auto', lender: null, principal: '12000.00', balance: '6000.00', repaidShare: '0.5000',
      monthlyPayment: '1000.00', currentRate: '0', endDate: '2027-01-15', balanceConverted: eur('6000.00'), monthlyPaymentConverted: eur('1000.00') }),
  ],
  months: [
    { month: '2026-10', total: eur('199564.23'), byLoan: [{ loanId: LOAN, balance: eur('193564.23') }, { loanId: CAR, balance: eur('6000.00') }] },
    { month: '2026-11', total: eur('197404.31'), byLoan: [{ loanId: LOAN, balance: eur('192404.31') }, { loanId: CAR, balance: eur('5000.00') }] },
    { month: '2027-01', total: eur('190000.00'), byLoan: [{ loanId: LOAN, balance: eur('190000.00') }, { loanId: CAR, balance: eur('0.00') }] },
    { month: '2027-02', total: eur('188000.00'), byLoan: [{ loanId: LOAN, balance: eur('188000.00') }] },
  ],
  events: [
    { date: '2027-01-15', code: 'realestate.loan_ends', loanId: CAR, phaseSeq: null, monthlyPaymentAfter: eur('1159.92') },
    { date: '2045-10-15', code: 'realestate.loan_ends', loanId: LOAN, phaseSeq: null, monthlyPaymentAfter: eur('0.00') },
  ],
  // The car starts with three months of interest only.
  spans: [
    { loanId: LOAN, start: '2025-10-15', end: '2045-10-15', phases: [
      { seq: 1, mode: 'annuity', from: '2025-11-15', to: '2045-10-15', annualRate: '0.035', monthlyPayment: '1159.92' }] },
    { loanId: CAR, start: '2024-01-01', end: '2027-01-15', phases: [
      { seq: 1, mode: 'deferred_partial', from: '2024-01-15', to: '2024-03-15', annualRate: '0', monthlyPayment: '0.00' },
      { seq: 2, mode: 'annuity', from: '2024-04-15', to: '2027-01-15', annualRate: '0', monthlyPayment: '1000.00' }] },
  ],
  steps: [
    { from: '2026-10', to: '2027-01', amount: eur('2159.92') },
    { from: '2027-02', to: '2045-10', amount: eur('1159.92') },
  ],
  byPerson: [
    { personId: ALICE, balance: eur('102782.12'), monthlyPayment: eur('1579.96'), loans: [
      { loanId: LOAN, shareBp: 5000, balance: eur('96782.12') }, { loanId: CAR, shareBp: 10000, balance: eur('6000.00') }] },
    { personId: BOB, balance: eur('96782.11'), monthlyPayment: eur('579.96'), loans: [{ loanId: LOAN, shareBp: 5000, balance: eur('96782.11') }] },
  ],
  ...over,
});

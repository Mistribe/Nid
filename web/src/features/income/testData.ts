import type { IncomeOverview, IncomePhase, IncomeSourceDetail, IncomeSourceList, IncomeSourceOverview, IncomeSourceSummary } from '@/api/income';

export const eur = (amount: string) => ({ amount, currency: 'EUR' });
export const SALARY = '0192f0aa-0000-7000-8000-00000000c001';

export const summary = (over: Partial<IncomeSourceSummary> = {}): IncomeSourceSummary => ({
  id: SALARY, displayName: 'Salaire Alice', kind: 'salary', payerLabel: 'Employeur', currency: 'EUR', visibility: 'private',
  monthlyEquivalent: eur('2450.00'), runningPhases: 2, gapSoon: [], canWrite: true, ended: false, endedOn: null, owners: [], ...over,
});

export const list = (items: IncomeSourceSummary[]): IncomeSourceList => ({ items });

export const phase = (over: Partial<IncomePhase> = {}): IncomePhase => ({
  id: 'p1', label: 'Salaire de base', startsOn: '2025-01-01', endsOn: null, netAmount: eur('2300.00'),
  every: { count: 1, unit: 'month' }, grossAmount: null, maxCycles: null, note: null, status: 'running',
  monthlyEquivalent: eur('2300.00'), version: 0, ...over,
});

/** Two running components (2 300 € monthly, 1 800 € yearly) and the base salary's previous, past phase. */
export const detail = (over: Partial<IncomeSourceDetail> = {}): IncomeSourceDetail => ({
  id: SALARY, displayName: 'Salaire Alice', kind: 'salary', payerLabel: 'Employeur', note: null, currency: 'EUR',
  visibility: 'private', canWrite: true, ended: false, endedOn: null, monthlyEquivalent: eur('2450.00'),
  phases: [
    phase(),
    phase({ id: 'p2', label: 'Prime annuelle', startsOn: '2024-12-15', netAmount: eur('1800.00'), every: { count: 1, unit: 'year' }, monthlyEquivalent: eur('150.00') }),
    phase({ id: 'p0', startsOn: '2024-01-01', endsOn: '2025-01-01', netAmount: eur('2200.00'), status: 'past', monthlyEquivalent: eur('2200.00') }),
  ],
  gapSoon: [], oneOffs: [], ...over,
});

export const sourceRow = (over: Partial<IncomeOverview['sources'][number]> = {}): IncomeOverview['sources'][number] => ({
  id: SALARY, module: 'income', name: 'Salaire Alice', kind: 'salary', visibility: 'private', owners: [], perMonth: eur('2450.00'),
  change1y: 4.1, nextOn: '2026-11-30', nextAmount: eur('2300.00'), gapOn: null, ended: false, ...over,
});

/** A year overview: 6 420 € a month (+4.1 % over a year), two sources in the split, no 3-year history. */
export const overview = (over: Partial<IncomeOverview> = {}): IncomeOverview => ({
  period: 'year', from: '2026-01-01', to: '2026-12-31', currency: 'EUR',
  figures: {
    monthlyNow: eur('6420.00'), monthlyChange1y: 4.1, total: eur('77040.00'), elapsed: eur('64200.00'),
    changeVsPrevious: eur('3000.00'), changeVsPreviousPct: 4.1, change3y: null, change3yYearly: null,
    monthlyRealChange1y: { percent: 2.1, inflationTo: '2026-09', missing: null }, inflation: 2,
  },
  split: [
    { item: SALARY, name: 'Salaire Alice', kind: 'salary', total: eur('60000.00'), perMonth: eur('5000.00'), percent: 77.9 },
    { item: null, name: 'one_off', kind: 'one_off', total: eur('17040.00'), perMonth: eur('1420.00'), percent: 22.1 },
  ],
  nowSplit: [], months: [], sources: [], kinds: ['salary', 'rent'], missingRates: [], ...over,
});

/**
 * `detail()`'s overview: 2 450 € a month (+4.5 % over a year), the next payment, December 2024 and an upcoming January
 * 2025 drawn (four decimals, as the server sends month amounts), the base salary's raise, the bonus added, the start.
 */
export const sourceOverview = (over: Partial<IncomeSourceOverview> = {}): IncomeSourceOverview => ({
  id: SALARY, from: '2024-12-01', to: '2025-01-31', currency: 'EUR',
  figures: {
    monthlyNow: eur('2450.00'),
    monthlyParts: [{ item: SALARY, name: 'Prime annuelle', amount: eur('150.00') }, { item: SALARY, name: 'Salaire de base', amount: eur('2300.00') }],
    nextOn: '2026-11-01', nextAmount: eur('2300.00'), nextLabel: 'Salaire de base', change1y: 4.5, changeSinceStart: 11.4,
    receivedThisYear: eur('23400.00'), oneOffsThisYear: eur('400.00'), realChange1y: { percent: 2.5, inflationTo: '2026-09', missing: null }, inflation: 2,
  },
  months: [
    { year: 2024, month: 12, upcoming: false, parts: [{ label: 'Prime annuelle', amount: eur('1800.0000') }, { label: 'Salaire de base', amount: eur('2200.0000') }], oneOff: eur('0.0000'), total: eur('4000.0000') },
    { year: 2025, month: 1, upcoming: true, parts: [{ label: 'Salaire de base', amount: eur('2300.0000') }], oneOff: eur('400.0000'), total: eur('2700.0000') },
  ],
  changes: [
    { on: '2025-01-01', kind: 'change', phaseId: 'p1', label: 'Salaire de base', before: eur('2200.00'), after: eur('2300.00'), percent: 4.5, upcoming: false },
    { on: '2024-12-15', kind: 'added', phaseId: 'p2', label: 'Prime annuelle', before: null, after: eur('1800.00'), percent: null, upcoming: false },
    { on: '2024-01-01', kind: 'start', phaseId: 'p0', label: 'Salaire de base', before: null, after: eur('2200.00'), percent: null, upcoming: false },
  ],
  lastPayments: [
    { phaseId: 'p1', label: 'Salaire de base', on: '2026-10-01', amount: eur('2300.0000') },
    { phaseId: 'p1', label: 'Salaire de base', on: '2026-09-01', amount: eur('2300.0000') },
  ],
  lastOneOffs: [{ id: 'o1', occurredOn: '2026-09-03', amount: eur('400.00'), note: 'Prime de projet' }],
  withholding: [],
  ...over,
});

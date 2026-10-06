import type { IncomePhase, IncomeSourceDetail, IncomeSourceList, IncomeSourceSummary } from './api';

export const eur = (amount: string) => ({ amount, currency: 'EUR' });
export const SALARY = '0192f0aa-0000-7000-8000-00000000c001';

export const summary = (over: Partial<IncomeSourceSummary> = {}): IncomeSourceSummary => ({
  id: SALARY, displayName: 'Salaire Alice', kind: 'salary', payerLabel: 'Employeur', currency: 'EUR', visibility: 'private',
  monthlyEquivalent: eur('2450.00'), runningPhases: 2, gapSoon: [], canWrite: true, ended: false, endedOn: null, ...over,
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

import type { GoalDetail, GoalFunding, GoalList, GoalMoney, GoalSummary } from '@/api/goals';

export const eur = (amount: string): GoalMoney => ({ amount, currency: 'EUR' });

export const GOAL = '0192f0aa-0000-7000-8000-00000000a001';

export const summary = (over: Partial<GoalSummary> = {}): GoalSummary => ({
  id: GOAL, name: 'Voyage', kind: 'savings', owners: [], visibility: 'shared', closed: false, canWrite: true, targetDate: '2027-06-01',
  target: eur('5000.0000'), reached: eur('2000.0000'), progressPct: 40, remaining: eur('3000.0000'), monthlyNeeded: eur('200.0000'),
  monthlyPace: eur('150.0000'), projectedOn: '2027-09-01', status: 'behind', partial: false, flags: [], ...over,
});

export const list = (items: GoalSummary[]): GoalList => ({ items });

export const ENVELOPE = '0192f0aa-0000-7000-8000-00000000b001';

export const funding = (over: Partial<GoalFunding> = {}): GoalFunding => ({
  envelopeId: ENVELOPE, name: 'PEA Alice', shareBp: 5000, value: eur('4000.0000'), contribution: eur('2000.0000'), asOf: '2026-10-01',
  stale: false, estimated: false, flags: [], ...over,
});

export const detail = (over: Partial<GoalDetail> = {}, goal: Partial<GoalSummary> = {}): GoalDetail => ({
  goal: summary(goal), months: null, note: null, funding: [funding()], entries: [], commitments: [], version: 0, ...over,
});

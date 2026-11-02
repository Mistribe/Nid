import type { Allocation, AllocationLine, DashboardEnvelope, DashboardEvent, DashboardFigure, DashboardPerson, DashboardTodo, DashboardView, HouseholdPoint, NetWorthHistory, NetWorthPointView, Projection } from './api';

const eur = (amount: string) => ({ amount, currency: 'EUR' });

export const figure = (amount: string | null, over: Partial<DashboardFigure> = {}): DashboardFigure => ({
  perCurrency: amount ? [eur(amount)] : [], total: amount ? eur(amount) : null, missingCurrency: null, ...over,
});

export const dashboard = (over: Partial<DashboardView> = {}): DashboardView => ({
  currency: 'EUR',
  netWorth: figure('12900.00'), valuesAsOf: '2025-09-30', envelopes: 2, envelopesWithoutValue: 0, staleEnvelopes: 0, estimatedEnvelopes: 0,
  commitments: figure('250.56'), cashThisMonth: figure('120.00'), subscriptions: 3,
  income: figure('2500.00'), incomeSources: 1, margin: eur('2249.45'), committedPct: 10,
  change: figure('1000.00'), changeContributions: figure('1000.00'), changePct: 9.1,
  envelopeSplit: [], people: [], peopleMissingCurrency: null, upcoming: [], todo: [], envelopesWithoutStart: 0,
  commitmentSources: [{ source: 'subscriptions', amortized: eur('250.56'), estimated: false }], allocation: null,
  ...over,
});

export const emptyDashboard = dashboard({
  netWorth: figure(null), valuesAsOf: null, envelopes: 0, commitments: figure(null), cashThisMonth: figure(null),
  subscriptions: 0, income: figure(null), incomeSources: 0, margin: null, committedPct: null,
  change: figure(null), changeContributions: figure(null), changePct: null, commitmentSources: [],
});

export const envelope = (over: Partial<DashboardEnvelope> = {}): DashboardEnvelope => ({
  id: '00000000-0000-7000-8000-0000000000e1', module: 'investments', name: 'PEA',
  value: eur('25000.00'), converted: eur('25000.00'), missingCurrency: null, asOf: '2025-09-30', stale: false, estimated: false, valuedOn: '2025-09-30', ...over,
});

export const person = (over: Partial<DashboardPerson> = {}): DashboardPerson => ({
  id: '00000000-0000-7000-8000-0000000000a1', name: 'Alice', slot: 0, amount: eur('12500.00'), pct: 50, ...over,
});

export const commitment = (source: string, amortized: string) => ({ source, amortized: eur(amortized), estimated: false });

export const event = (over: Partial<DashboardEvent> = {}): DashboardEvent => ({
  on: '2025-11-10', kind: 'subscription_renewal', id: '00000000-0000-7000-8000-0000000000c1', module: 'subscriptions',
  name: 'Amazon Prime', label: null, amount: eur('69.90'), ...over,
});

export const todoItem = (over: Partial<DashboardTodo> = {}): DashboardTodo => ({
  kind: 'stale_value', id: '00000000-0000-7000-8000-0000000000e2', module: 'investments', name: 'Livret A',
  on: '2025-06-03', label: null, ...over,
});

export const curvePoint = (over: Partial<NetWorthPointView> = {}): NetWorthPointView => ({
  on: '2025-09-30', value: eur('1000.00'), contributions: eur('900.00'), missingCurrency: null, ...over,
});

export const netWorth = (points: NetWorthPointView[], period = '12m'): NetWorthHistory => ({ currency: 'EUR', period, points });

export const allocationLine = (key: string, amount: string, pct: number, over: Partial<AllocationLine> = {}): AllocationLine => ({
  key, amount: eur(amount), pct, targetPct: null, gapPct: null, gap: null, ...over,
});

/** Issue #104: a savings account split between a euro fund and an ETF, a home, and a loan. */
export const allocation = (over: Partial<Allocation> = {}): Allocation => ({
  assets: eur('325000.00'), debts: eur('-2000.00'), approximate: eur('9375.00'),
  classes: [allocationLine('equity', '9375.00', 2.9), allocationLine('capital_guaranteed', '15625.00', 4.8), allocationLine('real_estate', '300000.00', 92.3)],
  liquidity: [allocationLine('available', '25000.00', 7.7), allocationLine('locked', '300000.00', 92.3)],
  target: {}, ...over,
});

export const projectionPoint = (on: string, over: Partial<HouseholdPoint> = {}): HouseholdPoint => ({
  on, nominal: '10000.00', real: '9500.00', savings: '4000.00', property: '8000.00', debt: '-2000.00',
  realSavings: '3800.00', realProperty: '7600.00', realDebt: '-1900.00', ...over,
});

/** The projection of a household with savings: today (Oct 2026), two December 31sts and a last month end. */
export const projection = (over: Partial<Projection> = {}): Projection => ({
  currency: 'EUR', horizon: '10y', horizonOn: '2028-03-31',
  assumptions: { currency: 'EUR', classRates: { equity: '6.0', cash: '1.5' }, inflation: '2.0', monthlySavings: null, savingsClass: 'equity', freedPaymentsToSavings: true },
  monthlySavings: '500.00', savingsOrigin: 'auto',
  points: [
    projectionPoint('2026-10-27', { nominal: '10000.00', real: '10000.00', realSavings: '4000.00', realProperty: '8000.00', realDebt: '-2000.00' }),
    projectionPoint('2026-12-31', {
      nominal: '11000.00', real: '10800.00', savings: '4500.00', property: '8300.00', debt: '-1800.00',
      realSavings: '4400.00', realProperty: '8150.00', realDebt: '-1750.00',
    }),
    projectionPoint('2027-12-31', { nominal: '17000.00', real: '16100.00', savings: '9000.00', property: '8700.00', debt: '-700.00' }),
    projectionPoint('2028-03-31', { nominal: '18000.00', real: '17000.00', savings: '10500.00', property: '8900.00', debt: '-600.00' }),
  ],
  milestones: [], warnings: [], retirementHorizons: [],
  ...over,
});

export const emptyProjection = (): Projection => projection({
  points: [projectionPoint('2026-10-27', { nominal: '0', real: '0', savings: '0', property: '0', debt: '0', realSavings: '0', realProperty: '0', realDebt: '0' })],
});

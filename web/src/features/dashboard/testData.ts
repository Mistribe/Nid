import type { DashboardEnvelope, DashboardEvent, DashboardFigure, DashboardPerson, DashboardTodo, DashboardView, NetWorthHistory, NetWorthPointView } from './api';

const eur = (amount: string) => ({ amount, currency: 'EUR' });

export const figure = (amount: string | null, over: Partial<DashboardFigure> = {}): DashboardFigure => ({
  perCurrency: amount ? [eur(amount)] : [], total: amount ? eur(amount) : null, missingCurrency: null, ...over,
});

export const dashboard = (over: Partial<DashboardView> = {}): DashboardView => ({
  currency: 'EUR',
  netWorth: figure('12900.00'), valuesAsOf: '2025-09-30', envelopes: 2, envelopesWithoutValue: 0, staleEnvelopes: 0,
  commitments: figure('250.56'), cashThisMonth: figure('120.00'), subscriptions: 3,
  income: figure('2500.00'), incomeSources: 1, margin: eur('2249.45'), committedPct: 10,
  change: figure('1000.00'), changeContributions: figure('1000.00'), changePct: 9.1,
  envelopeSplit: [], people: [], peopleMissingCurrency: null, upcoming: [], todo: [], envelopesWithoutStart: 0,
  commitmentSources: [{ source: 'subscriptions', amortized: eur('250.56') }],
  ...over,
});

export const emptyDashboard = dashboard({
  netWorth: figure(null), valuesAsOf: null, envelopes: 0, commitments: figure(null), cashThisMonth: figure(null),
  subscriptions: 0, income: figure(null), incomeSources: 0, margin: null, committedPct: null,
  change: figure(null), changeContributions: figure(null), changePct: null, commitmentSources: [],
});

export const envelope = (over: Partial<DashboardEnvelope> = {}): DashboardEnvelope => ({
  id: '00000000-0000-7000-8000-0000000000e1', module: 'investments', name: 'PEA',
  value: eur('25000.00'), converted: eur('25000.00'), missingCurrency: null, asOf: '2025-09-30', stale: false, ...over,
});

export const person = (over: Partial<DashboardPerson> = {}): DashboardPerson => ({
  id: '00000000-0000-7000-8000-0000000000a1', name: 'Alice', slot: 0, amount: eur('12500.00'), pct: 50, ...over,
});

export const commitment = (source: string, amortized: string) => ({ source, amortized: eur(amortized) });

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

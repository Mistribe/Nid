import type { DashboardFigure, DashboardView } from './api';

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
  envelopeSplit: [], people: [], peopleMissingCurrency: null, upcoming: [], todo: [],
  ...over,
});

export const emptyDashboard = dashboard({
  netWorth: figure(null), valuesAsOf: null, envelopes: 0, commitments: figure(null), cashThisMonth: figure(null),
  subscriptions: 0, income: figure(null), incomeSources: 0, margin: null, committedPct: null,
  change: figure(null), changeContributions: figure(null), changePct: null,
});

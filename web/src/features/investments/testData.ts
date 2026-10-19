import type { EnvelopeDetail, EnvelopeList, EnvelopeSummary, Figures } from './api';

export const eur = (amount: string) => ({ amount, currency: 'EUR' });

export const figures = (over: Partial<Figures> = {}): Figures => ({
  value: eur('25000.0000'), asOf: '2025-09-30', netContributions: eur('22000.0000'), gain: eur('3000.0000'),
  gainPct: 13.6, annualReturnPct: 14.2, fees: eur('0.0000'), gainAfterFees: eur('3000.0000'), income: eur('0.0000'),
  contributionsSince: eur('0.0000'), withdrawalsSince: eur('0.0000'), missingOpening: false, staleDays: null, ...over,
});

export const unknownFigures = (): Figures => ({
  value: null, asOf: null, netContributions: null, gain: null, gainPct: null, annualReturnPct: null, fees: null, gainAfterFees: null,
  income: null, contributionsSince: eur('0'), withdrawalsSince: eur('0'), missingOpening: false, staleDays: null,
});

export const PEA = '0192f0aa-0000-7000-8000-00000000e001';

export const summary = (over: Partial<EnvelopeSummary> = {}): EnvelopeSummary => ({
  id: PEA, displayName: 'PEA', kind: 'pea', currency: 'EUR', visibility: 'shared', closed: false, canWrite: true, figures: figures(), owners: [], ...over,
});

export const list = (items: EnvelopeSummary[]): EnvelopeList => ({ items });

export const detail = (over: Partial<EnvelopeDetail> = {}): EnvelopeDetail => ({
  id: PEA, displayName: 'PEA', kind: 'pea', currency: 'EUR', visibility: 'shared', closed: false, canWrite: true,
  institution: 'Banque', accountHint: '4521', openedOn: null, valuationCadence: 'monthly', staleAfterDays: null, note: null,
  figures: figures(),
  valuations: [
    { id: 'v1', asOf: '2024-12-31', amount: eur('21000.0000'), note: null },
    { id: 'v2', asOf: '2025-09-30', amount: eur('25000.0000'), note: null },
  ],
  flows: [
    { id: 'f1', occurredOn: '2024-12-31', kind: 'opening', amount: eur('20000.0000'), category: null, externalRef: null, note: null },
    { id: 'f2', occurredOn: '2025-06-15', kind: 'contribution', amount: eur('2000.0000'), category: null, externalRef: null, note: null },
  ],
  ...over,
});

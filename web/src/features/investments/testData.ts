import type { EnvelopeDetail, EnvelopeList, EnvelopeOverviewRow, EnvelopeSummary, Figures, InvestmentsOverview } from './api';

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

export const LIVRET = '0192f0aa-0000-7000-8000-00000000e002';
export const ALICE = '0192f0aa-0000-7000-8000-0000000000a1';

/** An overview with nothing: the default the page opens on in tests that do not look at it. */
export const overview = (over: Partial<InvestmentsOverview> = {}): InvestmentsOverview => ({
  currency: 'EUR', openCount: 0, closedCount: 0, value: null, oldestAsOf: null, staleCount: 0, withoutValueCount: 0,
  netContributions: null, paidIn: null, gain: null, gainPct: null, annualReturnPct: null, sinceJanuary: null, sinceJanuaryPct: null,
  withoutStartCount: 0, byKind: [], byPerson: [], envelopes: [], points: [], watch: [], findings: [], ...over,
});

export const overviewRow = (over: Partial<EnvelopeOverviewRow> = {}): EnvelopeOverviewRow => ({
  id: PEA, name: 'PEA', kind: 'pea', institution: 'Banque', owners: [{ personId: ALICE, shareBp: 10000 }], currency: 'EUR',
  figures: figures(), valueConverted: eur('25000.00'), gainConverted: eur('3000.00'), ...over,
});

/** A PEA and a savings account, the savings account's value old, with a year of points. */
export const household = (): InvestmentsOverview => overview({
  openCount: 2, value: eur('35000.00'), oldestAsOf: '2025-08-31', staleCount: 1, netContributions: eur('31800.00'), paidIn: eur('32000.00'),
  gain: eur('3200.00'), gainPct: 10, annualReturnPct: 6.2, sinceJanuary: eur('1840.00'), sinceJanuaryPct: 5.6,
  byKind: [{ kind: 'pea', value: eur('25000.00'), sharePct: 71.4 }, { kind: 'savings', value: eur('10000.00'), sharePct: 28.6 }],
  byPerson: [{ personId: ALICE, value: eur('35000.00'), gain: eur('3200.00'), sharePct: 100 }],
  envelopes: [
    overviewRow(),
    overviewRow({ id: LIVRET, name: 'Livret A', kind: 'savings', institution: null,
      figures: figures({ value: eur('10000.0000'), asOf: '2025-08-31', netContributions: eur('9800.0000'), gain: eur('200.0000'), gainPct: 2,
        annualReturnPct: 2.1, staleDays: 45 }),
      valueConverted: eur('10000.00'), gainConverted: eur('200.00') }),
  ],
  points: [
    { on: '2025-08-31', value: eur('33000.00'), netContributions: eur('31000.00'), byKind: [{ kind: 'pea', value: eur('23000.00') }, { kind: 'savings', value: eur('10000.00') }] },
    { on: '2025-09-30', value: eur('35000.00'), netContributions: eur('31800.00'), byKind: [{ kind: 'pea', value: eur('25000.00') }, { kind: 'savings', value: eur('10000.00') }] },
    { on: '2025-10-15', value: eur('35000.00'), netContributions: eur('31800.00'), byKind: [{ kind: 'pea', value: eur('25000.00') }, { kind: 'savings', value: eur('10000.00') }] },
  ],
  watch: [
    { code: 'stale_value', envelopeId: LIVRET, holdingId: null, on: '2025-08-31' },
    { code: 'holding_maturity', envelopeId: PEA, holdingId: '0192f0aa-0000-7000-8000-0000000000h1', on: '2025-12-05' },
  ],
});

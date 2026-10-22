import type { EnvelopeDetail, EnvelopeList, EnvelopeOverviewRow, EnvelopeSummary, Figures, HoldingOverviewRow, HouseholdHoldings, HouseholdPerformance, InvestmentsOverview } from './api';

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

/** A period with nothing counted: the default the Performance tab opens on in tests that do not look at it. */
export const performance = (over: Partial<HouseholdPerformance> = {}): HouseholdPerformance => ({
  currency: 'EUR', from: '2025-01-01', to: '2025-10-15', startValue: null, endValue: null, contributions: null, withdrawals: null,
  transfers: null, performance: null, performancePct: null, months: [], bestMonth: null, worstMonth: null, envelopes: [], excluded: [],
  reference: null, findings: [], ...over,
});

/** The PEA and the savings account over the year: +1 840, February the worst month. */
export const householdPerformance = (): HouseholdPerformance => performance({
  startValue: eur('31000.00'), endValue: eur('35000.00'), contributions: eur('2400.00'), withdrawals: eur('240.00'), transfers: eur('0.00'),
  performance: eur('1840.00'), performancePct: 5.5,
  months: [
    { month: '2025-01', performance: eur('640.00') }, { month: '2025-02', performance: eur('-310.00') },
    { month: '2025-03', performance: eur('1510.00') },
  ],
  bestMonth: '2025-03', worstMonth: '2025-02',
  envelopes: [
    { id: PEA, name: 'PEA', kind: 'pea', currency: 'EUR', performance: '1700.00', performanceConverted: eur('1700.00'), annualReturnPct: 8.4 },
    { id: LIVRET, name: 'Livret A', kind: 'savings', currency: 'EUR', performance: '140.00', performanceConverted: eur('140.00'), annualReturnPct: 2.6 },
  ],
  excluded: [{ envelopeId: '0192f0aa-0000-7000-8000-00000000e003', name: 'CTO', code: 'no_start_value' }],
  reference: { annualReturnPct: 2.6 },
});

export const WORLD_PEA = '0192f0aa-0000-7000-8000-0000000000c1';
export const WORLD_LIFE = '0192f0aa-0000-7000-8000-0000000000c2';
export const LIFE = '0192f0aa-0000-7000-8000-00000000e004';

/** Nothing detailed: the default the page reads in tests that do not look at the holdings. */
export const holdings = (over: Partial<HouseholdHoldings> = {}): HouseholdHoldings => ({
  currency: 'EUR', value: null, detailed: null, unallocated: null, undetailed: null, detailedPct: null, detailedCount: 0, envelopeCount: 0,
  byKind: [], ucSharePct: null, envelopesUc: [], holdings: [], concentrations: [], costYear: 2024, cost: null, costPct: null,
  withoutReportCount: 0, undetailedEnvelopes: [], watch: [], findings: [], envelopeFees: [], projection: null, ...over,
});

export const holdingRow = (over: Partial<HoldingOverviewRow>): HoldingOverviewRow => ({
  envelopeId: PEA, envelopeName: 'PEA', holdingId: WORLD_PEA, name: 'Amundi MSCI World', isin: 'IE00B4L5Y983', kind: 'etf', currency: 'EUR',
  asOf: '2025-09-30', value: '38900.00', valueConverted: eur('38900.00'), sharePct: 23.9, gainPct: 41.2, annualReturnPct: 9.8,
  totalFeesPct: null, implicitCost: null, maturesOn: null, ...over,
});

/** A PEA and a life insurance sharing an ETF, a savings account without holdings, a structured product maturing soon. */
export const householdHoldings = (over: Partial<HouseholdHoldings> = {}): HouseholdHoldings => holdings({
  value: eur('162850.00'), detailed: eur('139700.00'), unallocated: eur('200.00'), undetailed: eur('22950.00'), detailedPct: 85.8,
  detailedCount: 2, envelopeCount: 3, ucSharePct: 64, envelopesUc: [{ envelopeId: LIFE, ucSharePct: 64, floorPct: 60 }],
  byKind: [{ kind: 'etf', value: eur('79900.00'), sharePct: 57.2 }, { kind: 'euro_fund', value: eur('59800.00'), sharePct: 42.8 }],
  holdings: [
    holdingRow({}),
    holdingRow({ envelopeId: LIFE, envelopeName: 'Assurance-vie', holdingId: WORLD_LIFE, name: 'iShares MSCI World', value: '31400.00',
      valueConverted: eur('31400.00'), sharePct: 19.3, totalFeesPct: 0.85, implicitCost: eur('233.75') }),
    holdingRow({ envelopeId: LIFE, envelopeName: 'Assurance-vie', holdingId: '0192f0aa-0000-7000-8000-0000000000c3', name: 'Athena 2023',
      isin: null, kind: 'structured', value: '9000.00', valueConverted: eur('9000.00'), sharePct: 5.5, maturesOn: '2025-12-05' }),
  ],
  concentrations: [{ isin: 'IE00B4L5Y983', name: 'Amundi MSCI World', value: eur('70300.00'), sharePct: 43.2, envelopeIds: [PEA, LIFE] }],
  costYear: 2024, cost: eur('233.75'), costPct: 0.9, withoutReportCount: 2,
  undetailedEnvelopes: [{ envelopeId: LIVRET, name: 'Livret A', value: eur('22950.00') }],
  watch: [
    { code: 'unexplained_units', envelopeId: PEA, holdingId: WORLD_PEA, on: '2025-09-30' },
    { code: 'holding_maturity', envelopeId: LIFE, holdingId: '0192f0aa-0000-7000-8000-0000000000c3', on: '2025-12-05' },
  ],
  ...over,
});

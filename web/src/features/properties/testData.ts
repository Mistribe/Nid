import { LOAN } from '../loans/testData';
import type { CostRow, PropertyArrears, PropertiesCosts, PropertiesOverview, PropertiesRental, PropertiesValuation, PropertyDetail, PropertyList, PropertyRecurringCost, PropertySummary, PropertyReturn, PropertyTenancy } from '@/api/properties';

export const PROPERTY = '0192f0bb-0000-7000-8000-00000000e001';

/**
 * README §12's purchase — 250 000 € and 19 000 € of notary fees on 2020-06-15 — with 12 000 € of works and 1 500 € of
 * property tax, estimated 290 000 € on 2026-06-01 and financed by the loans' golden loan (193 564,23 € left): the
 * figures are what the server would write.
 */
export const propertySummary = (over: Partial<PropertySummary> = {}): PropertySummary => ({
  id: PROPERTY, name: 'Maison de Nantes', type: 'primary_residence', currency: 'EUR', visibility: 'shared', value: '290000.00',
  valueOn: '2026-06-01', stale: false, netValue: '96435.77', grossYield: null, vacancyRate: null, canWrite: true, owners: [], ...over,
});

export const propertyList = (items: PropertySummary[]): PropertyList => ({ items });

export const propertyDetail = (over: Partial<PropertyDetail> = {}): PropertyDetail => ({
  id: PROPERTY, name: 'Maison de Nantes', type: 'primary_residence', currency: 'EUR', visibility: 'shared',
  owners: [{ personId: '01926b9e-0000-7000-8000-00000000000a', shareBp: 10000 }],
  purchaseDate: '2020-06-15', purchasePrice: '250000.00', notaryFees: '19000.00', agencyFees: '0.00', otherAcquisitionCosts: '0.00',
  surfaceM2: '85.50', locationLabel: 'Nantes', valuationCadence: 'yearly', staleAfterDays: null, rentalStartsOn: null, note: null,
  receiptMonths: [], version: 7,
  figures: {
    value: '290000.00', valueOn: '2026-06-01', stale: false, costPrice: '281000.00', latentGain: '9000.00', linkedBalance: '193564.23',
    netValue: '96435.77', realCost: '288823.35',
    realCostLines: [
      { code: 'acquisition', amount: '269000.00' }, { code: 'loan_interest', amount: '6323.35' }, { code: 'loan_insurance', amount: '0.00' },
      { code: 'loan_fees', amount: '0.00' }, { code: 'works', amount: '12000.00' }, { code: 'property_tax', amount: '1500.00' },
    ],
    netCostOfRents: '288823.35', rentedSince: null, expectedRent: '0.00', rentReceived: '0.00', arrears: '0.00', vacantDays: null,
    vacancyRate: null, grossYield: null, netYieldBeforeTax: null,
  },
  warnings: [],
  // Oldest first, as the server lists them.
  valuations: [
    { id: 'v1', asOf: '2025-06-01', amount: '280000.00', note: null },
    { id: 'v2', asOf: '2026-06-01', amount: '290000.00', note: 'agence' },
  ],
  flows: [
    { id: 'f1', occurredOn: '2024-03-10', kind: 'expense', amount: '12000.00', category: 'works', note: 'toiture' },
    { id: 'f2', occurredOn: '2025-10-15', kind: 'expense', amount: '1500.00', category: 'property_tax', note: null },
  ],
  recurringCosts: [], tenancies: [],
  loans: [{ id: LOAN, name: 'Prêt maison', currency: 'EUR', balance: '193564.23', balanceConverted: '193564.23' }],
  canWrite: true, shareLevel: 'view', canChangeOwners: false, ...over,
});

/**
 * Let since 2021-01-01 at 900 € a month, the twelve rents of the last 12 months received, against the 281 000 € cost price
 * and the 1 500 € property tax of the window: the rental figures the server would write.
 */
export const rentedFigures = (over: Partial<PropertyDetail['figures']> = {}): PropertyDetail['figures'] => ({
  ...propertyDetail().figures, rentedSince: '2021-01-01', expectedRent: '10800.00', rentReceived: '10800.00', arrears: '0.00',
  vacantDays: 0, vacancyRate: '0.0000', grossYield: '0.0384', netYieldBeforeTax: '0.0331', ...over,
});

/** A tenancy under way since 2023-01-01, as the server writes it (3C2 §3). */
export const tenancy = (over: Partial<PropertyTenancy> = {}): PropertyTenancy => ({
  id: 't2', version: 3, label: 'T2 meublé', startsOn: '2023-01-01', endsOn: null, rentMonthly: '900.00', chargesMonthly: '50.00',
  deposit: '1800.00', note: 'bail de 3 ans', current: true, indexed: false, ...over,
});

/** A property tax of 1 200 € a year from 2021-10-15, as the server writes it: 100 € a month, next due on 2026-10-15 (3C2 §3). */
export const recurringCost = (over: Partial<PropertyRecurringCost> = {}): PropertyRecurringCost => ({
  id: 'c1', version: 1, category: 'property_tax', amount: '1200.00', every: { count: 1, unit: 'year' }, startsOn: '2021-10-15',
  endsOn: null, note: null, monthlyEquivalent: '100.00', nextDueOn: '2026-10-15', ...over,
});

export const RENTAL = '0192f0bb-0000-7000-8000-00000000e003';
export const OWNER = '01926b9e-0000-7000-8000-00000000000a';

const eur = (amount: string) => ({ amount, currency: 'EUR' });

/** An empty household's overview: what the page opens on by default. */
export const propertiesOverview = (over: Partial<PropertiesOverview> = {}): PropertiesOverview => ({
  currency: 'EUR', count: 0, value: null, oldestAsOf: null, staleCount: 0, withoutValueCount: 0, linkedBalance: eur('0.00'), loanCount: 0,
  netValue: null, netValuePct: null, costPrice: null, latentGain: null, latentGainPct: null, rentReceived: null, grossYield: null,
  byType: [], byPerson: [], properties: [], points: [], watch: [], findings: [], ...over,
});

/**
 * The home above (290 000 €, its golden loan, 96 435,77 € net) and a rental at 198 000 €, let at 780 € a month, without a
 * loan; the figures are what the server would write.
 */
export const householdOverview = (): PropertiesOverview => propertiesOverview({
  count: 2, value: eur('488000.00'), oldestAsOf: '2026-06-01', staleCount: 1, linkedBalance: eur('193564.23'), loanCount: 1,
  netValue: eur('294435.77'), netValuePct: 60.3, costPrice: eur('467300.00'), latentGain: eur('20700.00'), latentGainPct: 4.4,
  rentReceived: eur('9360.00'), grossYield: '0.0502',
  byType: [{ type: 'primary_residence', value: eur('290000.00'), sharePct: 59.4 }, { type: 'rental', value: eur('198000.00'), sharePct: 40.6 }],
  byPerson: [{ personId: OWNER, value: eur('488000.00'), linkedBalance: eur('193564.23'), netValue: eur('294435.77'), sharePct: 100 }],
  properties: [
    {
      id: PROPERTY, name: 'Maison de Nantes', type: 'primary_residence', currency: 'EUR', owners: [{ personId: OWNER, shareBp: 10000 }],
      value: '290000.00', valueOn: '2026-06-01', stale: false, linkedBalance: '193564.23', netValue: '96435.77', latentGain: '9000.00',
      grossYield: null, valueConverted: eur('290000.00'), netValueConverted: eur('96435.77'),
    },
    {
      id: RENTAL, name: 'T2 Villeurbanne', type: 'rental', currency: 'EUR', owners: [{ personId: OWNER, shareBp: 10000 }],
      value: '198000.00', valueOn: '2024-09-30', stale: true, linkedBalance: '0.00', netValue: '198000.00', latentGain: '11700.00',
      grossYield: '0.0502', valueConverted: eur('198000.00'), netValueConverted: eur('198000.00'),
    },
  ],
  points: [
    { on: '2026-08-31', value: eur('478000.00'), linkedBalance: eur('194300.00'), netValue: eur('283700.00') },
    { on: '2026-09-30', value: eur('488000.00'), linkedBalance: eur('193932.00'), netValue: eur('294068.00') },
    { on: '2026-10-22', value: eur('488000.00'), linkedBalance: eur('193564.23'), netValue: eur('294435.77') },
  ],
  watch: [
    { code: 'valuation_stale', propertyId: RENTAL, loanId: null, on: '2024-09-30', amount: null },
    { code: 'rent_arrears', propertyId: RENTAL, loanId: null, on: null, amount: eur('780.00') },
    { code: 'property_cost_due', propertyId: RENTAL, loanId: null, on: '2026-11-15', amount: eur('1120.00') },
  ],
});

/** No let property: what the page reads by default, so « Location » stays hidden. */
export const propertiesRental = (over: Partial<PropertiesRental> = {}): PropertiesRental => ({
  currency: 'EUR', from: '2025-10-23', to: '2026-10-22', count: 0, expectedRent: null, rentReceived: null, arrears: null, vacantDays: null,
  vacancyRate: null, grossYield: null, netYieldBeforeTax: null, notSignificantCount: 0, charges: null, loanPayments: null, cashFlow: null,
  cashFlowMonthly: null, months: [], properties: [], tenancies: [], findings: [], ...over,
});

const month = (m: string, rent: string, charges: string, loans: string, cash: string) =>
  ({ month: m, rentReceived: eur(rent), charges: eur(charges), loanPayments: eur(loans), cashFlow: eur(cash) });

/** The rental above let at 780 € a month with its loan, and a studio let since May; the figures are what the server would write. */
export const householdRental = (): PropertiesRental => propertiesRental({
  count: 2, expectedRent: eur('12510.00'), rentReceived: eur('12060.00'), arrears: eur('450.00'), vacantDays: 14, vacancyRate: '0.0259',
  grossYield: '0.0498', netYieldBeforeTax: '0.0438', notSignificantCount: 1, charges: eur('2920.00'), loanPayments: eur('9744.00'),
  cashFlow: eur('-604.00'), cashFlowMonthly: eur('-50.33'),
  months: [month('2025-10', '0.00', '1120.00', '0.00', '-1120.00'), month('2025-11', '780.00', '0.00', '812.00', '-32.00')],
  properties: [
    {
      id: RENTAL, name: 'T2 Villeurbanne', currency: 'EUR', currentTenancy: { label: 'Bail Martin', startsOn: '2024-03-01', endsOn: null },
      rentMonthly: '780.00', expectedRent: '9360.00', rentReceived: '9360.00', arrears: '0.00', vacancyRate: '0.0000', grossYield: '0.0498',
      netYieldBeforeTax: '0.0438', charges: '2920.00', loanPayments: '9744.00', cashFlow: '-3304.00', cashFlowConverted: eur('-3304.00'),
    },
    {
      id: PROPERTY, name: 'Studio', currency: 'EUR', currentTenancy: { label: 'Bail Dupont', startsOn: '2026-05-15', endsOn: '2026-11-30' },
      rentMonthly: '450.00', expectedRent: '3150.00', rentReceived: '2700.00', arrears: '450.00', vacancyRate: '0.0800', grossYield: null,
      netYieldBeforeTax: null, charges: '0.00', loanPayments: '0.00', cashFlow: '2700.00', cashFlowConverted: eur('2700.00'),
    },
  ],
  tenancies: [
    { propertyId: RENTAL, label: 'Bail Martin', startsOn: '2024-03-01', endsOn: null },
    { propertyId: PROPERTY, label: 'Bail Dupont', startsOn: '2026-05-15', endsOn: '2026-11-30' },
  ],
});

/** Nothing paid nor planned: what the page reads by default. */
export const propertiesCosts = (over: Partial<PropertiesCosts> = {}): PropertiesCosts => ({
  currency: 'EUR', from: '2025-10-23', to: '2026-10-22', paid: null, worksPaid: null, planned: null, plannedMonthly: null, byCategory: [],
  dues: [], properties: [], realCost: null, netCostOfRents: null, findings: [],
  byYear: { years: [2022, 2023, 2024, 2025, 2026].map((year) => ({ year, complete: year < 2026 })), categories: [] }, ...over,
});

const costRow = (id: string, name: string, over: Partial<CostRow>): CostRow => ({
  id, name, currency: 'EUR', paid: '0.00', plannedMonthly: '0.00', rentReceived: '0.00', loanPayment: '0.00', acquisition: '0.00', works: '0.00',
  loanInterest: '0.00', loanInsurance: '0.00', loanFees: '0.00', otherCharges: '0.00', realCost: '0.00', rentsSincePurchase: '0.00',
  netCostOfRents: '0.00', realCostConverted: null, ...over,
});

/** The home and the rental above, with their costs paid and planned; the figures are what the server would write. */
export const householdCosts = (): PropertiesCosts => propertiesCosts({
  paid: eur('9230.00'), worksPaid: eur('1800.00'), planned: eur('7180.00'), plannedMonthly: eur('598.00'),
  byCategory: [
    { category: 'condo_fees', paid: eur('3240.00'), planned: eur('3300.00') },
    { category: 'property_tax', paid: eur('3180.00'), planned: eur('3240.00') },
    { category: 'works', paid: eur('1800.00'), planned: null },
  ],
  dues: [
    { on: '2026-11-01', propertyId: PROPERTY, category: 'condo_fees', amount: '360.00', currency: 'EUR', amountConverted: eur('360.00') },
    { on: '2026-11-15', propertyId: RENTAL, category: 'property_tax', amount: '1140.00', currency: 'EUR', amountConverted: eur('1140.00') },
    { on: '2027-01-01', propertyId: RENTAL, category: 'insurance', amount: '180.00', currency: 'EUR', amountConverted: eur('180.00') },
  ],
  properties: [
    costRow(PROPERTY, 'Maison de Nantes', {
      paid: '5110.00', plannedMonthly: '285.00', loanPayment: '1395.00', acquisition: '269000.00', works: '12000.00', loanInterest: '6323.35',
      otherCharges: '1500.00', realCost: '288823.35', netCostOfRents: '288823.35', realCostConverted: eur('288823.35'),
    }),
    costRow(RENTAL, 'T2 Villeurbanne', {
      paid: '2850.00', plannedMonthly: '215.00', rentReceived: '9360.00', acquisition: '186300.00', otherCharges: '7400.00', realCost: '193700.00',
      rentsSincePurchase: '25740.00', netCostOfRents: '167960.00', realCostConverted: eur('193700.00'),
    }),
  ],
  realCost: eur('482523.35'), netCostOfRents: eur('456783.35'),
  // The rental bought in 2023 makes that year partial.
  byYear: {
    years: [2022, 2023, 2024, 2025, 2026].map((year) => ({ year, complete: year !== 2023 && year < 2026 })),
    categories: [
      { category: 'property_tax', cells: [
        { paid: eur('1400.00'), change: null }, { paid: eur('1450.00'), change: null }, { paid: eur('2900.00'), change: null },
        { paid: eur('3045.00'), change: '0.0500' }, { paid: eur('1600.00'), change: null },
      ] },
      { category: 'insurance', cells: [
        { paid: null, change: null }, { paid: null, change: null }, { paid: eur('400.00'), change: null },
        { paid: eur('380.00'), change: '-0.0500' }, { paid: null, change: null },
      ] },
    ],
  },
});

/** One overdue month and one still running, with the letter the server would build for the overdue one. */
export const arrears = (over: Partial<PropertyArrears> = {}): PropertyArrears => ({
  property: PROPERTY, propertyName: 'Maison de Nantes', currency: 'EUR', total: '1900.00', overdueTotal: '950.00',
  months: [
    { month: '2026-09', due: '950.00', imputed: '0.00', remaining: '950.00', overdue: true, inLetter: true },
    { month: '2026-10', due: '950.00', imputed: '0.00', remaining: '950.00', overdue: false, inLetter: false },
  ],
  letter: {
    total: '950.00', months: ['2026-09'], postalAddress: '12 rue des Lilas, 44000 Nantes', tenantNames: 'Jeanne Martin',
    landlordName: 'Paul Durand', landlordAddress: '3 quai Duguay, 44000 Nantes', missing: [], leavesOutEarlierTenant: false, formerTenant: false,
  },
  ...over,
});

/** No property: what the page reads by default. */
export const propertiesValuation = (over: Partial<PropertiesValuation> = {}): PropertiesValuation => ({
  currency: 'EUR', count: 0, value: null, costPrice: null, latentGain: null, latentGainPct: null, surfaceM2: null, valuePerM2: null,
  withoutSurfaceCount: 0, upToDateCount: 0, staleCount: 0, withoutValueCount: 0, properties: [], findings: [], ...over,
});

/** The home above (85,5 m²) and the rental, stale and without a surface; the figures are what the server would write. */
export const householdValuation = (): PropertiesValuation => propertiesValuation({
  count: 2, value: eur('488000.00'), costPrice: eur('467300.00'), latentGain: eur('20700.00'), latentGainPct: 4.4, surfaceM2: '85.50',
  valuePerM2: eur('3392.00'), withoutSurfaceCount: 1, upToDateCount: 1, staleCount: 1,
  properties: [
    {
      id: PROPERTY, name: 'Maison de Nantes', type: 'primary_residence', currency: 'EUR', purchaseDate: '2020-06-15', surfaceM2: '85.50',
      value: '290000.00', valueOn: '2026-06-01', stale: false, cadence: 'yearly', costPrice: '281000.00', latentGain: '9000.00', latentGainPct: 3.2,
      valuePerM2: '3392.00', annualPacePct: 0.5, valueConverted: eur('290000.00'),
      estimates: [{ on: '2025-06-01', value: '280000.00' }, { on: '2026-06-01', value: '290000.00' }],
      costSteps: [{ on: '2020-06-15', costPrice: '269000.00' }, { on: '2021-03-01', costPrice: '281000.00' }],
    },
    {
      id: RENTAL, name: 'T2 Villeurbanne', type: 'rental', currency: 'EUR', purchaseDate: '2023-12-05', surfaceM2: null,
      value: '198000.00', valueOn: '2024-09-30', stale: true, cadence: 'yearly', costPrice: '186300.00', latentGain: '11700.00', latentGainPct: 6.3,
      valuePerM2: null, annualPacePct: null, valueConverted: eur('198000.00'),
      estimates: [{ on: '2024-09-30', value: '198000.00' }], costSteps: [{ on: '2023-12-05', costPrice: '186300.00' }],
    },
  ],
});

/** A rental's return: a scenario sold in 2033 at the Pinel's end, sixteen resale years from 2026, the rate by default. */
export const propertyReturn = (over: Partial<PropertyReturn> = {}): PropertyReturn => ({
  assumptions: {
    yearsAfterPinel: 0, resalePrice: null, valueGrowth: '0.0100', rentGrowth: '0.0100', chargesGrowth: '0.0200', vacancyMonths: 0,
    rentAfterPinel: null, marginalRate: '0.3000', resaleFees: '0.0500',
    defaulted: ['yearsAfterPinel', 'resalePrice', 'valueGrowth', 'rentGrowth', 'chargesGrowth', 'vacancyMonths', 'rentAfterPinel', 'marginalRate', 'resaleFees'],
  },
  scenario: {
    outcome: { resaleOn: '2033-11-02', resalePrice: '214000.00', irr: '3.4', netGain: '41250.00', pinelClawback: false },
    priceSource: 'growth', savingsEffort: '18500.00',
    breakdown: [{ line: 'rent', realized: '45000.00', projected: '60000.00' }, { line: 'resale', realized: '0.00', projected: '214000.00' }],
  },
  sensitivity: Array.from({ length: 16 }, (_, k) => ({
    resaleOn: `${2026 + k}-11-02`, resalePrice: `${200000 + k * 2000}.00`, irr: k === 0 ? null : '2.5', netGain: `${k * 1000}.00`, pinelClawback: 2026 + k < 2033,
  })),
  pinelEnd: '2033-01-01',
  warnings: [{ code: 'marginal_rate_default', loanId: null }],
  ...over,
});

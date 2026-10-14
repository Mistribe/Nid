import { LOAN } from '../loans/testData';
import type { PropertyDetail, PropertyList, PropertySummary, PropertyTenancy } from './api';

export const PROPERTY = '0192f0bb-0000-7000-8000-00000000e001';

/**
 * README §12's purchase — 250 000 € and 19 000 € of notary fees on 2020-06-15 — with 12 000 € of works and 1 500 € of
 * property tax, estimated 290 000 € on 2026-06-01 and financed by the loans' golden loan (193 564,23 € left): the
 * figures are what the server would write.
 */
export const propertySummary = (over: Partial<PropertySummary> = {}): PropertySummary => ({
  id: PROPERTY, name: 'Maison de Nantes', type: 'primary_residence', currency: 'EUR', visibility: 'shared', value: '290000.00',
  valueOn: '2026-06-01', stale: false, netValue: '96435.77', grossYield: null, vacancyRate: null, canWrite: true, ...over,
});

export const propertyList = (items: PropertySummary[]): PropertyList => ({ items });

export const propertyDetail = (over: Partial<PropertyDetail> = {}): PropertyDetail => ({
  id: PROPERTY, name: 'Maison de Nantes', type: 'primary_residence', currency: 'EUR', visibility: 'shared',
  owners: [{ personId: '01926b9e-0000-7000-8000-00000000000a', shareBp: 10000 }],
  purchaseDate: '2020-06-15', purchasePrice: '250000.00', notaryFees: '19000.00', agencyFees: '0.00', otherAcquisitionCosts: '0.00',
  surfaceM2: '85.50', locationLabel: 'Nantes', valuationCadence: 'yearly', staleAfterDays: null, rentalStartsOn: null, note: null,
  version: 7,
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
  canWrite: true, ...over,
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
  deposit: '1800.00', note: 'bail de 3 ans', current: true, ...over,
});

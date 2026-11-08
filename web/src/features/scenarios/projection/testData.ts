import type { HouseholdPoint, Projection } from '@/api/dashboard';
import { projection, projectionPoint } from '@/features/dashboard/testData';
import type { ProjectionComparison } from './api';

export const ALICE = '01926b9e-0000-7000-8000-00000000000a';
export const LOAN_A = '0192f0aa-0000-7000-8000-0000000000e1';
export const LOAN_B = '0192f0aa-0000-7000-8000-0000000000e2';
export const FLAT = '0192f0aa-0000-7000-8000-0000000000f1';
export const PURCHASE = '0192f0aa-0000-7000-8000-0000000000b1';

export const persons = [{ id: ALICE, kind: 'adult', displayName: 'Alice', email: null, birthDate: '1980-05-02', retirementAge: 64 }];

export const loans = {
  items: [
    { id: LOAN_A, name: 'Prêt immo', lender: null, currency: 'EUR', visibility: 'shared', principal: '200000.00', remainingBalance: '150000.00', canWrite: true, ended: false },
    { id: LOAN_B, name: 'Prêt travaux', lender: null, currency: 'EUR', visibility: 'shared', principal: '20000.00', remainingBalance: '8000.00', canWrite: true, ended: false },
  ],
};

export const properties = {
  items: [{ id: FLAT, name: 'Appartement Lyon', type: 'rental', currency: 'EUR', visibility: 'shared', stale: false, canWrite: true, owners: [] }],
};

export const scenarioList = {
  items: [
    { id: PURCHASE, kind: 'property_purchase', name: 'Studio Lyon', use: 'rental', owners: [], visibility: 'shared' },
    { id: '0192f0aa-0000-7000-8000-0000000000b2', kind: 'projection', name: 'Mi-temps', owners: [], visibility: 'shared' },
  ],
};

const scenarioPoint = (on: string, over: Partial<HouseholdPoint> = {}) => projectionPoint(on, { nominal: '12000.00', real: '11500.00', ...over });

/** The base and the scenario on the same grid, the scenario 25 000 € ahead at the horizon. */
export const comparison = (over: Partial<ProjectionComparison> = {}): ProjectionComparison => {
  const base: Projection = projection({ retirementHorizons: [{ person: ALICE, name: 'Alice', on: '2044-05-31' }] });
  const scenario: Projection = projection({
    points: [scenarioPoint('2026-10-27'), scenarioPoint('2026-12-31'), scenarioPoint('2027-12-31'), scenarioPoint('2028-03-31', { nominal: '43000.00', real: '41000.00' })],
    milestones: [{ on: '2027-03-15', kind: 'event', label: 'Remboursement anticipé' }],
    warnings: [{ code: 'event_in_past', detail: '1' }],
  });
  const gaps = [
    { on: '2026-10-27', nominal: '2000.00', real: '1500.00' }, { on: '2026-12-31', nominal: '1000.00', real: '700.00' },
    { on: '2027-12-31', nominal: '-5000.00', real: '-4600.00' }, { on: '2028-03-31', nominal: '25000.00', real: '24000.00' },
  ];
  return { base, scenario, gaps, gapNominal: '25000.00', gapReal: '24000.00', document: { version: 1, horizon: '10y', events: [] }, ...over };
};

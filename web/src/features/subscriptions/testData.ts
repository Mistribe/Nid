import type { components } from '@/api/schema';
import { aliceSession } from '@/test/fixtures';
import type { Detail } from './api';

export const ALICE = aliceSession.person.id;
export const BOB = '01926b9e-0000-7000-8000-00000000000b';
export const SUB = '0192f0aa-0000-7000-8000-000000000001';
export const PHASE = '0192f0aa-0000-7000-8000-0000000000a1';
export const PURCHASE = '0192f0aa-0000-7000-8000-0000000000b1';

export const eur = (amount: string) => ({ amount, currency: 'EUR' });

export const persons: components['schemas']['PersonDto'][] = [
  { id: ALICE, kind: 'adult', displayName: 'Alice Dev', email: 'alice@example.test' },
  { id: BOB, kind: 'adult', displayName: 'Bob Dev', email: 'bob@example.test' },
];

/** A shared monthly subscription owned by Alice, with a gift card: what the detail page and its edits read. */
export function detail(over: Partial<Detail> = {}): Detail {
  return {
    id: SUB, displayName: 'Spotify Premium Duo', provider: null, autoRenew: true, cancelledAt: null,
    expiresOn: null, nextChargeOn: '2026-01-28', nextChargeAmount: eur('17.20'),
    cashCost: [eur('17.20')], amortizedCost: [eur('17.20')], cashCostConverted: null, amortizedCostConverted: null,
    convertedIsEstimate: false, freeDays: 92,
    coverage: [
      { start: '2025-06-28', endBefore: '2025-10-28', source: 'paid', isProjected: false },
      { start: '2025-10-28', endBefore: '2026-01-28', source: 'gift', isProjected: false },
      { start: '2026-01-28', endBefore: '2026-02-28', source: 'paid', isProjected: true },
    ],
    charges: [
      { date: '2025-09-28', price: eur('17.20'), isProjected: false, purchaseId: null },
      { date: '2026-01-28', price: eur('17.20'), isProjected: true, purchaseId: null },
    ],
    findings: [],
    visibility: 'shared',
    owners: [{ personId: ALICE, shareBp: 10000 }],
    users: [ALICE],
    note: null,
    phases: [{
      id: PHASE, startsOn: '2025-06-28', endsOn: null, price: eur('17.20'),
      every: { count: 1, unit: 'month' }, covers: { count: 1, unit: 'month' }, maxCycles: null,
      payer: { type: 'household', personId: null }, note: null,
    }],
    purchases: [{
      id: PURCHASE, date: '2025-10-10', label: 'Carte cadeau 3 mois', price: eur('0.00'), payer: { type: 'external', personId: null },
      kind: 'gift', allocatedAmount: null,
      grants: [{ subscriptionId: SUB, duration: { count: 3, unit: 'month' }, source: 'gift', startsOn: null }], note: null,
    }],
    canWrite: true,
    ...over,
  };
}

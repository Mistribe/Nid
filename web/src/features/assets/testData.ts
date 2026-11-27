import type { AssetList, AssetRow, AssetView, OwnershipCost } from '@/api/assets';

export const ASSET = '0192f0bb-0000-7000-8000-000000000001';
const OWNER = [{ personId: '01926b9e-0000-7000-8000-00000000000a', shareBp: 10000 }];

export const row = (over: Partial<AssetRow> = {}): AssetRow => ({
  id: ASSET, name: 'Citadine', kind: 'vehicle', value: { amount: '8000.00', currency: 'EUR' }, valueOn: '2026-06-01',
  valueFromPurchase: false, currency: 'EUR', visibility: 'shared', owners: OWNER, archived: false, valueEstimated: false, valueBase: null, ...over,
});

// Each asset in a group of its own unless the test names the groups: nothing gathered.
export const list = (rows: AssetRow[], groups?: AssetList['groups'], kinds: string[] = []): AssetList =>
  ({ rows, total: rows.length, kinds, groups: groups ?? rows.map((r) => ({ owners: r.owners, items: [r.id], value: r.value })) });

export const assetView = (over: Partial<AssetView> = {}): AssetView => ({
  ...row(), purchasePrice: { amount: '12000.00', currency: 'EUR' }, purchasedOn: '2022-03-01', note: null, weightGrams: null, fineness: null,
  registeredOn: null, mileageKm: null, mileageOn: null, fiscalHp: null, energy: null, depreciationRate: null, mileageReadings: [],
  valuations: [{ id: 'v1', asOf: '2026-06-01', amount: { amount: '8000.00', currency: 'EUR' }, note: 'cote', fromBank: false }],
  subscriptions: [], fxMissing: false, valueStale: false, hasCosts: false, version: 1, canWrite: true, shareLevel: 'edit', ...over,
});

/** #650: a vehicle's cost of ownership over the last 12 months, with every part filled. */
export const ownershipCost = (over: Partial<OwnershipCost> = {}): OwnershipCost => ({
  from: '2025-12-01', to: '2026-11-30', total: { amount: '4640.00', currency: 'EUR' }, monthly: { amount: '386.99', currency: 'EUR' },
  perKm: { amount: '0.39', currency: 'EUR' }, incomplete: false, years: [2026, 2025],
  parts: [
    { code: 'running:service', amount: { amount: '600.00', currency: 'EUR' }, sourceId: null, name: null },
    { code: 'running:fuel', amount: { amount: '1200.00', currency: 'EUR' }, sourceId: null, name: null },
    { code: 'subscription', amount: { amount: '600.00', currency: 'EUR' }, sourceId: 'sub-1', name: 'Assurance auto' },
    { code: 'credit', amount: { amount: '240.00', currency: 'EUR' }, sourceId: 'loan-1', name: 'Crédit auto' },
    { code: 'depreciation', amount: { amount: '2000.00', currency: 'EUR' }, sourceId: null, name: null },
  ],
  ...over,
});

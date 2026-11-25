import type { AssetRow, AssetView } from '@/api/assets';

export const ASSET = '0192f0bb-0000-7000-8000-000000000001';
const OWNER = [{ personId: '01926b9e-0000-7000-8000-00000000000a', shareBp: 10000 }];

export const row = (over: Partial<AssetRow> = {}): AssetRow => ({
  id: ASSET, name: 'Citadine', kind: 'vehicle', value: { amount: '8000.00', currency: 'EUR' }, valueOn: '2026-06-01',
  valueFromPurchase: false, currency: 'EUR', visibility: 'shared', owners: OWNER, archived: false, valueEstimated: false, valueBase: null, ...over,
});

export const assetView = (over: Partial<AssetView> = {}): AssetView => ({
  ...row(), purchasePrice: { amount: '12000.00', currency: 'EUR' }, purchasedOn: '2022-03-01', note: null, weightGrams: null, fineness: null,
  registeredOn: null, mileageKm: null, mileageOn: null, fiscalHp: null, energy: null, depreciationRate: null,
  valuations: [{ id: 'v1', asOf: '2026-06-01', amount: { amount: '8000.00', currency: 'EUR' }, note: 'cote', fromBank: false }],
  subscriptions: [], fxMissing: false, hasCosts: false, version: 1, canWrite: true, shareLevel: 'edit', ...over,
});

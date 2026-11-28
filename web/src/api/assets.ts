import type { QueryClient } from '@tanstack/react-query';
import type { components } from '@/api/schema';
import { DASHBOARD } from './dashboard';

export type AssetRow = components['schemas']['AssetRow'];
export type AssetList = components['schemas']['AssetListView'];
export type AssetView = components['schemas']['AssetView'];
export type LinkedSubscription = components['schemas']['LinkedSubscriptionView'];
export type AssetValuation = components['schemas']['ValuationView'];
export type AssetCosts = components['schemas']['AssetCostsView'];
export type AssetCost = components['schemas']['AssetCostView'];
export type OwnershipCost = components['schemas']['OwnershipCostView'];
export type CostPart = components['schemas']['CostPartView'];
export type CreateAssetBody = components['schemas']['CreateAssetBody'];
export type EditAssetBody = components['schemas']['EditAssetBody'];

export const ASSETS = '/api/v1/assets' as const;
export const ASSET = '/api/v1/assets/{id}' as const;
export const ASSET_ARCHIVE = '/api/v1/assets/{id}/archive' as const;
export const ASSET_UNARCHIVE = '/api/v1/assets/{id}/unarchive' as const;
export const ASSET_VALUATIONS = '/api/v1/assets/{id}/valuations' as const;
export const ASSET_VALUATION = '/api/v1/assets/{id}/valuations/{valuationId}' as const;
export const ASSET_COSTS = '/api/v1/assets/{id}/costs' as const;
export const ASSET_COST = '/api/v1/assets/{id}/costs/{flowId}' as const;
export const ASSET_OWNERSHIP_COST = '/api/v1/assets/{id}/ownership-cost' as const;
export const ASSET_MILEAGE_READING = '/api/v1/assets/{id}/mileage-readings/{on}' as const;

/** The categories of a vehicle's running cost, in the order the form offers them. */
export const ASSET_COST_CATEGORIES = ['service', 'repair', 'tyres', 'fuel', 'other'] as const;

/** The kinds of an asset, in the order the form offers them. */
export const ASSET_ENERGIES = ['petrol', 'diesel', 'hybrid', 'plug_in_hybrid', 'electric', 'lpg', 'other'] as const;
export const ASSET_KINDS = ['vehicle', 'precious_metal', 'art', 'collectible', 'other'] as const;

/** Every write on an asset invalidates the list, every detail and the home's net worth. */
export function invalidateAssets(queryClient: QueryClient): Promise<void> {
  return Promise.all([ASSETS, ASSET, ASSET_COSTS, ASSET_OWNERSHIP_COST, DASHBOARD].map((path) => queryClient.invalidateQueries({ queryKey: ['get', path] }))).then(() => undefined);
}

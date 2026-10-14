import type { QueryClient } from '@tanstack/react-query';
import type { components } from '@/api/schema';

export type PropertySummary = components['schemas']['PropertySummary'];
export type PropertyList = components['schemas']['PropertyList'];
export type PropertyDetail = components['schemas']['PropertyDetail'];
export type PropertyWarning = components['schemas']['PropertyWarningView'];
export type PropertyValuation = components['schemas']['PropertyValuationView'];
export type PropertyFlow = components['schemas']['PropertyFlowView'];
export type PropertyTenancy = components['schemas']['PropertyTenancyView'];

export const PROPERTIES = '/api/v1/realestate/properties' as const;
export const PROPERTY = '/api/v1/realestate/properties/{id}' as const;
export const VALUATIONS = '/api/v1/realestate/properties/{id}/valuations' as const;
export const VALUATION = '/api/v1/realestate/properties/{id}/valuations/{valuationId}' as const;
export const FLOWS = '/api/v1/realestate/properties/{id}/flows' as const;
export const FLOW = '/api/v1/realestate/properties/{id}/flows/{flowId}' as const;
export const TENANCIES = '/api/v1/realestate/properties/{id}/tenancies' as const;
export const TENANCY = '/api/v1/realestate/properties/{id}/tenancies/{tenancyId}' as const;

/** Every write on a property invalidates the list and every detail (3D1 design §6), as do the loan link's (§7). */
export function invalidateProperties(queryClient: QueryClient): Promise<void> {
  return Promise.all([PROPERTIES, PROPERTY].map((path) => queryClient.invalidateQueries({ queryKey: ['get', path] }))).then(() => undefined);
}

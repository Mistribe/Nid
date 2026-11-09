import type { QueryClient } from '@tanstack/react-query';
import type { components } from '@/api/schema';
import { DASHBOARD } from './dashboard';
import { OVERVIEW } from './income';
import { SERIES_CATALOGUE, SERIES_POINTS } from './series';
import { TAX_SALE } from './tax';

export type PropertySummary = components['schemas']['PropertySummary'];
export type PropertyArrears = components['schemas']['PropertyArrearsView'];
export type PropertyList = components['schemas']['PropertyList'];
export type PropertyDetail = components['schemas']['PropertyDetail'];
export type PropertyWarning = components['schemas']['PropertyWarningView'];
export type PropertyValuation = components['schemas']['PropertyValuationView'];
export type PropertyFlow = components['schemas']['PropertyFlowView'];
export type PropertyTenancy = components['schemas']['PropertyTenancyView'];
export type PropertyRecurringCost = components['schemas']['PropertyRecurringCostView'];
export type PropertiesOverview = components['schemas']['PropertiesOverview'];
export type PropertyOverviewRow = components['schemas']['PropertyOverviewRow'];
export type PropertyPoint = components['schemas']['PropertyPointView'];
export type PropertyWatch = components['schemas']['PropertyWatchView'];
export type PropertiesRental = components['schemas']['PropertiesRental'];
export type RentalRow = components['schemas']['RentalRow'];
export type RentalMonth = components['schemas']['RentalMonthView'];
export type PropertiesCosts = components['schemas']['PropertiesCosts'];
export type CostRow = components['schemas']['CostRow'];
export type CostDue = components['schemas']['CostDueView'];
export type PropertiesValuation = components['schemas']['PropertiesValuation'];
export type ValuationRow = components['schemas']['ValuationRow'];
export type PropertyReturn = components['schemas']['PropertyReturnView'];
export type ReturnAssumptionsBody = components['schemas']['PropertyReturnAssumptionsBody'];

export const PROPERTIES = '/api/v1/realestate/properties' as const;
export const PROPERTY = '/api/v1/realestate/properties/{id}' as const;
export const PROPERTIES_OVERVIEW = '/api/v1/realestate/properties/overview' as const;
export const PROPERTIES_RENTAL = '/api/v1/realestate/properties/overview/rental' as const;
export const PROPERTIES_COSTS = '/api/v1/realestate/properties/overview/costs' as const;
export const PROPERTIES_VALUATION = '/api/v1/realestate/properties/overview/valuation' as const;
export const VALUATIONS = '/api/v1/realestate/properties/{id}/valuations' as const;
export const VALUATION = '/api/v1/realestate/properties/{id}/valuations/{valuationId}' as const;
export const FLOWS = '/api/v1/realestate/properties/{id}/flows' as const;
export const FLOW = '/api/v1/realestate/properties/{id}/flows/{flowId}' as const;
export const TENANCIES = '/api/v1/realestate/properties/{id}/tenancies' as const;
export const TENANCY = '/api/v1/realestate/properties/{id}/tenancies/{tenancyId}' as const;
export const RECURRING_COSTS = '/api/v1/realestate/properties/{id}/recurring-costs' as const;
export const RECURRING_COST = '/api/v1/realestate/properties/{id}/recurring-costs/{costId}' as const;
export const PROPERTY_ARREARS = '/api/v1/realestate/properties/{id}/arrears' as const;
export const PROPERTY_RECEIPTS = '/api/v1/realestate/properties/{id}/receipts' as const;
export const PROPERTY_RETURN = '/api/v1/realestate/properties/{id}/return' as const;
export const RETURN_ASSUMPTIONS = '/api/v1/realestate/properties/{id}/return-assumptions' as const;

/**
 * Every write on a property invalidates the list, the overview's tabs and every detail (3D1 design §6), as do the loan link's
 * (§7); the income page and the home income too, which count the rents (income dashboard §5).
 */
export function invalidateProperties(queryClient: QueryClient): Promise<void> {
  return Promise.all([PROPERTIES, PROPERTIES_OVERVIEW, PROPERTIES_RENTAL, PROPERTIES_COSTS, PROPERTIES_VALUATION, PROPERTY, PROPERTY_RECEIPTS, PROPERTY_ARREARS, PROPERTY_RETURN, OVERVIEW, DASHBOARD, TAX_SALE, SERIES_POINTS, SERIES_CATALOGUE].map((path) => queryClient.invalidateQueries({ queryKey: ['get', path] }))).then(() => undefined);
}

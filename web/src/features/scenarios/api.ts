import type { components } from '@/api/schema';

export type Scenario = components['schemas']['ScenarioView'];
export type ScenarioListItem = components['schemas']['ScenarioListItemView'];
export type ScenarioResult = components['schemas']['ScenarioResultView'];
export type PropertyPurchaseDoc = components['schemas']['PropertyPurchaseView'];
export type ScenarioDefaults = components['schemas']['ScenarioDefaultsView'];

export const SCENARIOS = '/api/v1/scenarios' as const;
export const SCENARIO = '/api/v1/scenarios/{id}' as const;
export const SCENARIO_SIMULATE = '/api/v1/scenarios/simulate' as const;
export const SCENARIO_DEFAULTS = '/api/v1/scenarios/defaults' as const;

export type ProjectionScenario = components['schemas']['ProjectionScenarioView'];
export type ProjectionScenarioBody = components['schemas']['ProjectionScenarioBody'];

export const PROJECTION_SCENARIOS = '/api/v1/scenarios/projection' as const;
export const PROJECTION_SCENARIO = '/api/v1/scenarios/projection/{id}' as const;
export const PROJECTION_SIMULATE = '/api/v1/scenarios/projection/simulate' as const;

export type ScenarioCompare = components['schemas']['ProjectionCompareView'];
export const SCENARIO_COMPARE = '/api/v1/scenarios/compare' as const;

export type SaleScenario = components['schemas']['SaleScenarioView'];
export type SaleResult = components['schemas']['SaleResultView'];
export type SaleDocument = components['schemas']['SaleScenarioDocView'];

export const SALE_SCENARIOS = '/api/v1/scenarios/sale' as const;
export const SALE_SCENARIO = '/api/v1/scenarios/sale/{id}' as const;

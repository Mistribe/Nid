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

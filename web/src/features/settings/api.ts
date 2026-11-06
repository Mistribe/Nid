/** Routes of the projection settings (household assumptions, a person's retirement). */
export const PROJECTION_ASSUMPTIONS = '/api/v1/core/projection-assumptions' as const;
export const INFLATION = '/api/v1/core/inflation' as const;
export const PERSON_RETIREMENT = '/api/v1/core/persons/{id}/retirement' as const;
/** The projection the dashboard reads: stale once either setting changes. */
export { PROJECTION } from '../dashboard/api';

/** The asset classes in display order. */
export const ASSET_CLASSES = ['cash', 'money_market', 'bond', 'equity', 'mixed', 'capital_guaranteed', 'real_estate', 'other'] as const;

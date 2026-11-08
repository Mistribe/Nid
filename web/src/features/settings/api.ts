/** Routes of the projection settings (household assumptions, a person's retirement). */
export const PROJECTION_ASSUMPTIONS = '/api/v1/core/projection-assumptions' as const;
export const INFLATION = '/api/v1/core/inflation' as const;
export const PERSON_RETIREMENT = '/api/v1/core/persons/{id}/retirement' as const;
/** The projection the dashboard reads: stale once either setting changes. */
export { PROJECTION } from '@/api/dashboard';

/** The asset classes in display order. */
export const ASSET_CLASSES = ['cash', 'money_market', 'bond', 'equity', 'mixed', 'capital_guaranteed', 'real_estate', 'other'] as const;

/** The classes whose rate can be typed year by year. */
export const YEARLY_CLASSES = ['cash', 'money_market'] as const;

/** The calendar years a yearly rate can be typed for: this one and the next four (a list of labels, no amount). */
export const projectionYears = () => { const year = new Date().getFullYear(); return [0, 1, 2, 3, 4].map((i) => String(year + i)); };

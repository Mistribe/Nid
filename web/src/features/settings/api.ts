import type { components } from '@/api/schema';

/** Routes of the projection settings (household assumptions, the market profiles and their effect, a person's retirement). */
export const PROJECTION_ASSUMPTIONS = '/api/v1/core/projection-assumptions' as const;
export const PROJECTION_PROFILES = '/api/v1/core/projection-assumptions/profiles' as const;
export const INFLATION = '/api/v1/core/inflation' as const;
export const PERSON_RETIREMENT = '/api/v1/core/persons/{id}/retirement' as const;
/** The projection the dashboard reads: stale once either setting changes. */
export { PROJECTION } from '@/api/dashboard';

export type Assumptions = components['schemas']['ProjectionAssumptionsView'];
export type AssumptionsPatch = components['schemas']['AssumptionsPatchBody'];

/** The asset classes in display order. */
export const ASSET_CLASSES = ['cash', 'money_market', 'bond', 'equity', 'mixed', 'capital_guaranteed', 'real_estate', 'other'] as const;

/** The classes whose rate can be typed year by year. */
export const YEARLY_CLASSES = ['cash', 'money_market'] as const;

/** The calendar years a yearly rate can be typed for: this one and the next four (a list of labels, no amount). */
export const projectionYears = () => { const year = new Date().getFullYear(); return [0, 1, 2, 3, 4].map((i) => String(year + i)); };

/** A server percent as a French one: « 1.5 » reads « 1,5 % » (a string swap, nothing computed). */
export const percent = (value: string) => `${value.replace('.', ',')} %`;

/** A typed percent as the wire string: a French comma reads as a point, nothing else is touched (the server checks it). */
export const wire = (text: string) => text.trim().replace(',', '.');

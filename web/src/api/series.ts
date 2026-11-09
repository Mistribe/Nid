import type { components } from '@/api/schema';

export type SeriesPoints = components['schemas']['SeriesPointsView'];
export type SeriesInfo = components['schemas']['SeriesInfo'];
export type SeriesPointsItem = components['schemas']['SeriesPointsItemView'];

export const SERIES_POINTS = '/api/v1/series/points' as const;
export const SERIES_CATALOGUE = '/api/v1/series' as const;

import type { components } from '@/api/schema';

export type RetirementView = components['schemas']['RetirementView'];
export type Departure = components['schemas']['DepartureView'];

export const RETIREMENT = '/api/v1/retirement' as const;

import type { components } from '@/api/schema';

export type TaxEstimate = components['schemas']['TaxEstimateView'];
export type TaxProfile = components['schemas']['TaxProfileView'];
export type TaxInputs = components['schemas']['TaxInputsView'];
export type TaxResult = components['schemas']['TaxResultView'];
export type PinelCommitment = components['schemas']['PinelCommitment'];

export const TAX = '/api/v1/tax' as const;
export const TAX_PROFILE = '/api/v1/tax/profile/{year}' as const;
export const TAX_SIMULATE = '/api/v1/tax/simulate' as const;

export const STATUSES = ['non_executive', 'executive', 'public'] as const;

export type Rental2044 = components['schemas']['Rental2044View'];

export const TAX_2044 = '/api/v1/tax/2044' as const;

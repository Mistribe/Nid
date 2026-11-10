import type { components } from '@/api/schema';

/** Issue #12: the year's review, net worth and investments, for the Foyer / Moi scope. */
export const YEAR_REVIEW = '/api/v1/reports/year' as const;

export type YearReview = components['schemas']['YearReviewView'];
export type NetWorthReview = components['schemas']['NetWorthReviewView'];
export type InvestmentsReview = components['schemas']['InvestmentsReviewView'];
export type InvestmentsReviewRow = components['schemas']['InvestmentsReviewRowView'];
export type ReviewPart = components['schemas']['ReviewPartView'];

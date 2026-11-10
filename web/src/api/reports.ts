import type { components } from '@/api/schema';

/** Issue #12: the year's review, part by part, for the Foyer / Moi scope. */
export const YEAR_REVIEW = '/api/v1/reports/year' as const;

export type YearReview = components['schemas']['YearReviewView'];
export type NetWorthReview = components['schemas']['NetWorthReviewView'];
export type InvestmentsReview = components['schemas']['InvestmentsReviewView'];
export type InvestmentsReviewRow = components['schemas']['InvestmentsReviewRowView'];
export type ReviewPart = components['schemas']['ReviewPartView'];
export type IncomeReview = components['schemas']['IncomeReviewView'];
export type IncomeReviewSource = components['schemas']['IncomeSourceView'];
export type EngagementReviewSource = components['schemas']['EngagementSourceView'];
export type SubscriptionsReview = components['schemas']['SubscriptionsReviewView'];
export type SubscriptionReviewItem = components['schemas']['SubscriptionReviewItemView'];
export type SubscriptionPriceChange = components['schemas']['SubscriptionPriceChangeView'];
export type LoansReview = components['schemas']['LoansReviewView'];
export type LoanReviewRow = components['schemas']['LoanReviewRowView'];
export type PropertiesReview = components['schemas']['PropertiesReviewView'];
export type PropertyReviewRow = components['schemas']['PropertyReviewRowView'];
export type GoalsReview = components['schemas']['GoalsReviewView'];
export type GoalReviewRow = components['schemas']['GoalReviewRowView'];
export type TaxReview = components['schemas']['TaxReviewView'];

import type { components } from '@/api/schema';

/** #85: a month's (or a period's) spending by category, and the lines behind one of its figures. */
export const SPENDING = '/api/v1/banking/spending' as const;
export const SPENDING_LINES = '/api/v1/banking/spending/lines' as const;
export type Spending = components['schemas']['SpendingView'];
export type SpendingCategory = components['schemas']['SpendingCategoryView'];
export type SpendingLines = components['schemas']['SpendingLines'];

/** What both endpoints read: the scope, and one month or a period of months (`yyyy-mm`). */
export type SpendingQuery = { scope: 'me' | 'household' } & ({ month: string } | { from: string; to: string });

/** The server's page size for the lines. */
export const LINES_PAGE = 50;

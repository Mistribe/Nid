import type { TFunction } from 'i18next';
import { formatLocalDate } from '@/components/LocalDate';
import type { PropertyDetail, PropertyWarning } from './api';

export const PROPERTY_TYPES = ['primary_residence', 'secondary_residence', 'rental', 'parking', 'land', 'other'] as const;
export const CADENCES = ['yearly', 'manual'] as const;
export const FLOW_KINDS = ['expense', 'income'] as const;
/** README §4's convention, in the server's order (`PropertyCategories`). */
export const CATEGORIES = {
  expense: ['works', 'property_tax', 'condo_fees', 'insurance', 'energy', 'maintenance', 'management_fees', 'other'],
  income: ['rent', 'recoverable_charges', 'other_income'],
} as const;
const WARNINGS = ['no_valuation', 'valuation_stale', 'loan_other_currency', 'fx_missing', 'visibility_mismatch', 'rental_without_tenancy'] as const;
/** A planned cost's categories (3C2 §2): the expense ones but works, already in the cost price, in the server's order. */
export const RECURRING_CATEGORIES = ['property_tax', 'condo_fees', 'insurance', 'energy', 'maintenance', 'management_fees', 'other'] as const;
/** A planned cost comes every few months or years (3C2 §2). */
export const EVERY_UNITS = ['month', 'year'] as const;

/** The real cost's own lines (3C1's `CostLine`); every other line is an expense category. */
const COST_LINES = ['acquisition', 'loan_interest', 'loan_insurance', 'loan_fees'] as const;

export type FlowKind = (typeof FLOW_KINDS)[number];
export type FlowCategory = (typeof CATEGORIES)[FlowKind][number];
const ALL_CATEGORIES: readonly FlowCategory[] = [...CATEGORIES.expense, ...CATEGORIES.income];

/** A kind's categories, typed as the union of all of them so both lists render alike. */
export const categoriesOf = (kind: FlowKind): readonly FlowCategory[] => CATEGORIES[kind];

const known = <T extends string>(values: readonly T[], value: string | null | undefined): T | undefined => values.find((v) => v === value);

// A value the server added later reads as its raw name rather than breaking the page (as the loans' texts).
export function typeText(t: TFunction, value: string): string {
  const k = known(PROPERTY_TYPES, value);
  return k ? t(`property_form.type_option.${k}`) : value;
}

export function kindText(t: TFunction, value: string): string {
  const k = known(FLOW_KINDS, value);
  return k ? t(`property_flows.kind_option.${k}`) : value;
}

export function categoryText(t: TFunction, value: string | null | undefined): string {
  const k = known(ALL_CATEGORIES, value);
  return k ? t(`property_flows.category_option.${k}`) : (value ?? '');
}

/** A line of the real cost: the acquisition and the loans' parts by their own name, then one per expense category. */
export function costLineText(t: TFunction, code: string): string {
  const k = known(COST_LINES, code);
  return k ? t(`property_detail.cost_line.${k}`) : categoryText(t, code);
}

/**
 * Spec §6: each warning in French. A warning about a loan names it from the detail's linked loans (the server warns
 * only about readable ones); the currency is the property's; a stale estimate is dated by the value's own date.
 */
export function warningText(t: TFunction, warning: PropertyWarning, property: PropertyDetail): string {
  const k = known(WARNINGS, warning.code);
  if (!k) return warning.code;
  const name = property.loans.find((l) => l.id === warning.loanId)?.name ?? '';
  const date = property.figures.valueOn ? formatLocalDate(property.figures.valueOn) : '';
  return t(`property_detail.warning.${k}`, { name, currency: property.currency, date });
}

/** A planned cost's period in words: « tous les mois », « tous les 3 mois », « tous les ans »; an unknown unit reads raw. */
export function everyText(t: TFunction, every: { count: number; unit: string }): string {
  const k = known(EVERY_UNITS, every.unit);
  return k ? t(`property_costs.every_${k}`, { count: every.count }) : `${every.count} ${every.unit}`;
}

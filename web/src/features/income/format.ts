import type { TFunction } from 'i18next';

export const KINDS = ['salary', 'self_employment', 'pension', 'unemployment', 'family_allowance', 'social_benefit', 'alimony', 'other'] as const;
export const STATUSES = ['running', 'upcoming', 'past'] as const;

/** A kind the server added later reads as its raw name rather than breaking the page (as investments' kindText). */
export function kindText(t: TFunction, kind: string): string {
  const known = KINDS.find((k) => k === kind);
  return known ? t(`income.kind.${known}`) : kind;
}

export function statusText(t: TFunction, status: string): string {
  const known = STATUSES.find((s) => s === status);
  return known ? t(`income.status.${known}`) : status;
}

/** A phase's cadence in words; a unit the screen does not know reads as its raw form. */
export function cadenceText(t: TFunction, every: { count: number; unit: string }): string {
  if (every.unit === 'year' && every.count === 1) return t('income.cadence.yearly');
  if (every.unit === 'month' && every.count === 1) return t('income.cadence.monthly');
  if (every.unit === 'month' && every.count === 3) return t('income.cadence.quarterly');
  if (every.unit === 'month') return t('income.cadence.months', { count: every.count });
  return `${every.count} ${every.unit}`;
}

import type { components } from '@/api/schema';

export type MoneyValue = components['schemas']['MoneyView'];

const DECIMAL = /^-?(0|[1-9]\d*)(\.\d+)?$/;

/**
 * Formats a decimal string exactly: Intl.NumberFormat accepts a numeric string (ECMA-402, NumberFormat v3)
 * and never turns it into a binary double. Nothing here computes (frontend/README.md §2, principle 1).
 */
export function formatMoney(value: MoneyValue, locale = 'fr-FR'): string {
  if (!DECIMAL.test(value.amount)) throw new RangeError(`Not a decimal string: ${JSON.stringify(value.amount)}`);
  return new Intl.NumberFormat(locale, { style: 'currency', currency: value.currency }).format(value.amount as `${number}`);
}

/** Proportional figures by default; tabular only in columns and on axes (design-system.md §3). */
export function Money({ value, tabular = false }: { value: MoneyValue; tabular?: boolean }) {
  return <span className={tabular ? 'tabular-nums' : undefined}>{formatMoney(value)}</span>;
}

import type { components } from '@/api/schema';

export type MoneyValue = components['schemas']['MoneyView'];

const DECIMAL = /^-?(0|[1-9]\d*)(\.\d+)?$/;

/**
 * A decimal string as currency, its thousands set apart by a no-break space: French takes a narrow one (U+202F), which
 * Inter draws about 2 px wide at text size, so « 1 000,00 € » read as « 1000,00 € » (design-system.md §3). Intl accepts
 * a numeric string (ECMA-402, NumberFormat v3) and never turns it into a binary double.
 */
export function formatCurrency(amount: string, currency: string, options: Intl.NumberFormatOptions = {}, locale = 'fr-FR'): string {
  return new Intl.NumberFormat(locale, { ...options, style: 'currency', currency }).formatToParts(amount as `${number}`)
    .map((part) => (part.type === 'group' ? ' ' : part.value)).join('');
}

/** Formats a decimal string exactly. Nothing here computes (frontend/README.md §2, principle 1). */
export function formatMoney(value: MoneyValue, locale = 'fr-FR'): string {
  if (!DECIMAL.test(value.amount)) throw new RangeError(`Not a decimal string: ${JSON.stringify(value.amount)}`);
  return formatCurrency(value.amount, value.currency, {}, locale);
}

/** Proportional figures by default; tabular only in columns and on axes (design-system.md §3). */
export function Money({ value, tabular = false }: { value: MoneyValue; tabular?: boolean }) {
  return <span className={tabular ? 'tabular-nums' : undefined}>{formatMoney(value)}</span>;
}

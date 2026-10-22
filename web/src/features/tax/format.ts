import type { TFunction } from 'i18next';
import '@/locales/tax';

export const REGIMES = ['auto', 'micro', 'real'] as const;
const WARNINGS = ['rules_carried_forward', 'micro_over_ceiling', 'pinel_requires_real', 'high_income_contribution'] as const;
/** A server amount that is zero (`0.00`, `-0`). */
export const ZERO = /^-?0(\.0+)?$/;
const NOTICES = ['missing_gross', 'unsupported_income', 'withdrawal_not_estimated', 'other_currency', 'not_a_declarant'] as const;

/** A code the server added later reads as its raw name rather than breaking the page (as income's kindText). */
export function regimeText(t: TFunction, regime: string): string {
  const known = REGIMES.find((r) => r === regime);
  return known ? t(`tax.regime.${known}`) : regime;
}

export function warningText(t: TFunction, code: string, year: number): string {
  const known = WARNINGS.find((w) => w === code);
  return known ? t(`tax.warning.${known}`, { year }) : code;
}

export function noticeText(t: TFunction, code: string, name: string, amount: string): string {
  const known = NOTICES.find((n) => n === code);
  return known ? t(`tax.notice.${known}`, { name, amount }) : `${name} : ${code}`;
}

/** A percent string from the server (`"4.5"`), formatted as is (`4,5 %`): Intl reads the decimal string, nothing computes. */
export function percentText(t: TFunction, value: string): string {
  return t('tax.percent', { value: new Intl.NumberFormat('fr-FR', { maximumFractionDigits: 2 }).format(value as `${number}`) });
}

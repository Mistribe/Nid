import type { TFunction } from 'i18next';
import type tax from '@/locales/fr.tax.json';
import '@/locales/tax';

export const REGIMES = ['auto', 'micro', 'real'] as const;
const WARNINGS = ['rules_carried_forward', 'micro_over_ceiling', 'pinel_requires_real', 'high_income_contribution'] as const;
/** A server amount that is zero (`0.00`, `-0`). */
export const ZERO = /^-?0(\.0+)?$/;
const NOTICES = ['missing_gross', 'unsupported_income', 'withdrawal_not_estimated', 'other_currency', 'not_a_declarant', 'rental_charges_removed', 'rental_fee_not_counted', 'rental_to_check', 'contribution_rate_differs', 'per_without_contributions', 'childcare_from_statements', 'childcare_projected', 'childcare_estimated'] as const;

/** A code the server added later reads as its raw name rather than breaking the page (as income's kindText). */
export function regimeText(t: TFunction, regime: string): string {
  const known = REGIMES.find((r) => r === regime);
  return known ? t(`tax.regime.${known}`) : regime;
}

/** What a tax term means, in plain words (design-system §6.2): the InfoTip beside it on every tax screen. */
export const tipText = (t: TFunction, key: keyof typeof tax.tax.tip) => t(`tax.tip.${key}`);

/** The PER warnings are told by the PER block, from the declarants' own figures. */
export const PER_WARNINGS: readonly string[] = ['per_over_ceiling', 'per_ceiling_estimated'];

export function warningText(t: TFunction, code: string, year: number): string {
  const known = WARNINGS.find((w) => w === code);
  return known ? t(`tax.warning.${known}`, { year }) : code;
}

export function noticeText(t: TFunction, code: string, name: string, amount: string, validated?: number | null, estimated?: number | null): string {
  const known = NOTICES.find((n) => n === code);
  return known ? t(`tax.notice.${known}`, { name, amount, validated, estimated }) : `${name} : ${code}`;
}

/** The notice's figure: a percent for the contribution-rate warning, euros (through `money`) for every other. */
export function noticeAmount(t: TFunction, code: string, amount: string, money: (amount: string) => string): string {
  return code === 'contribution_rate_differs' ? percentText(t, amount) : money(amount);
}

/** A number of parts from the server (`"2.50"`) in French (`2,5`): Intl reads the decimal string, nothing computes. */
export const partsText = (value: string) => new Intl.NumberFormat('fr-FR', { maximumFractionDigits: 2 }).format(value as `${number}`);

export const METHODS = ['entered', 'estimated', 'mixed'] as const;
export const ORIGINS = ['entered', 'gross', 'net'] as const;

/** A percent string from the server (`"4.5"`), formatted as is (`4,5 %`): Intl reads the decimal string, nothing computes. */
export function percentText(t: TFunction, value: string): string {
  return t('tax.percent', { value: new Intl.NumberFormat('fr-FR', { maximumFractionDigits: 2 }).format(value as `${number}`) });
}

const LINES = ['211', '215', '221', '222', '223', '224', '227', '229', '230', '240', '250', '263', '420', '435', '438', '440', '441', '4BA', '4BE'] as const;
/** The form's deficit lines for the 4BC and 4BB cases: 435 and 438 when the interest exceeds the gross rents, 440 and 441 otherwise. */
export function deficitLines(interestDeficit: string): readonly [string, string] {
  return ZERO.test(interestDeficit) ? ['440', '441'] : ['435', '438'];
}

/** The lines the spec attaches a note to (§3). */
export const NOTED_LINES = ['227', '230'] as const;

/** A line number the server added later reads as itself. */
export function lineText(t: TFunction, code: string): string {
  const known = LINES.find((l) => l === code);
  return known ? t(`tax.d2044.line.${known}`) : code;
}

export function lineNote(t: TFunction, code: string): string | null {
  const known = NOTED_LINES.find((l) => l === code);
  return known ? t(`tax.d2044.note.${known}`) : null;
}

import type { TFunction } from 'i18next';
import { formatLocalDate } from '@/components/LocalDate';
import { formatMoney } from '@/components/Money';
import type { LoanDetail, LoanWarning } from './api';
import { fractionToPercent } from './rate';

export const PHASE_MODES = ['annuity', 'deferred_partial', 'deferred_total'] as const;
export const RATE_TYPES = ['fixed', 'variable'] as const;
export const INSURANCE_MODES = ['fixed_monthly', 'rate_on_initial', 'rate_on_remaining'] as const;
export const ROUNDINGS = ['half_up', 'truncate'] as const;
export const FIRST_PERIODS = ['full', 'prorata_actual_365'] as const;
export const FEE_KINDS = ['file', 'guarantee', 'early_repayment_penalty', 'renegotiation', 'other'] as const;
export const EFFECTS = ['reduce_term', 'reduce_payment'] as const;
export const WARNINGS = ['projection_uses_last_rate', 'exceeds_total_payments', 'event_off_payment_date', 'override_ignored_after_early_repayment', 'bank_schedule_unfit'] as const;

const known = <T extends string>(values: readonly T[], value: string): T | undefined => values.find((v) => v === value);

/** An amount of the loan in its currency, exactly as the server wrote it: the loan's amounts are bare decimal strings. */
export const money = (currency: string, amount: string) => formatMoney({ amount, currency });
/** An amount shown with its sign, negative included (3B2 design §8: a net saving): formatting only, never arithmetic. */
export const signedMoney = (currency: string, amount: string) =>
  new Intl.NumberFormat('fr-FR', { style: 'currency', currency, signDisplay: 'exceptZero' }).format(amount as `${number}`);

/** A rate of the API (a fraction) in percent: « 3,5 % ». */
export const percent = (t: TFunction, rate: string) => t('loans.percent', { rate: fractionToPercent(rate) });

// A value the server added later reads as its raw name rather than breaking the page (as income's kindText).
export function modeText(t: TFunction, value: string): string {
  const k = known(PHASE_MODES, value);
  return k ? t(`loan_form.mode_option.${k}`) : value;
}

export function rateTypeText(t: TFunction, value: string): string {
  const k = known(RATE_TYPES, value);
  return k ? t(`loan_form.rate_type_option.${k}`) : value;
}

export function insuranceModeText(t: TFunction, value: string): string {
  const k = known(INSURANCE_MODES, value);
  return k ? t(`loan_form.insurance_mode_option.${k}`) : value;
}

export function roundingText(t: TFunction, value: string): string {
  const k = known(ROUNDINGS, value);
  return k ? t(`loan_form.rounding_option.${k}`) : value;
}

export function firstPeriodText(t: TFunction, value: string): string {
  const k = known(FIRST_PERIODS, value);
  return k ? t(`loan_form.first_period_option.${k}`) : value;
}

export function feeKindText(t: TFunction, value: string): string {
  const k = known(FEE_KINDS, value);
  return k ? t(`loan_detail.fee_kind.${k}`) : value;
}

export function effectText(t: TFunction, value: string): string {
  const k = known(EFFECTS, value);
  return k ? t(`loan_detail.effect.${k}`) : value;
}

/**
 * Spec §7: each warning in French with its payment number; row 0 is a repayment before the first payment (precision 10);
 * 3F2's `schedule_mismatch` names the date the calculation leaves the bank's table, read from the loan's comparison.
 */
export function warningText(t: TFunction, warning: LoanWarning, loan?: LoanDetail): string {
  if (warning.code === 'event_off_payment_date' && warning.row === 0) return t('loan_detail.warning.event_before_first_payment');
  if (warning.code === 'schedule_mismatch' && loan?.comparison?.divergesOn)
    return t('loan_detail.warning.schedule_mismatch', { date: formatLocalDate(loan.comparison.divergesOn) });
  const k = known(WARNINGS, warning.code);
  return k ? t(`loan_detail.warning.${k}`, { n: warning.row }) : warning.code;
}

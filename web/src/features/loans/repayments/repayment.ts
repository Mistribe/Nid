import type { TFunction } from 'i18next';
import type { DefaultValues } from 'react-hook-form';
import { z } from 'zod';
import type { components } from '@/api/schema';
import { ISO_DATE, toFieldAmount, toWireAmount } from '@/lib/amount';
import { todayIso } from '@/lib/today';
import type { LoanRepayment } from '@/api/loans';
import type { EFFECTS } from '../format';
import { LOAN_AMOUNT } from '../new/loanForm';
import '@/locales/investments';
import '@/locales/loans';
import '@/locales/forms';

export type Effect = (typeof EFFECTS)[number];

/** The add form opened from a simulation's column (3B2 design §8), as typed there; `seq` tells two presses apart. */
export type RepaymentPrefill = { onDate: string; amount: string; effect: Effect; penalty: string; seq: number };

const optionalAmount = (v: string) => v.trim() === '' || LOAN_AMOUNT.test(v.trim());

/** 3B2 design §7: a date, a positive amount, an effect the person picks, an optional penalty, a note. */
export function repaymentSchema(t: TFunction) {
  return z.object({
    onDate: z.string().regex(ISO_DATE, t('edit.required')),
    amount: z.string().trim().regex(LOAN_AMOUNT, t('loan_form.amount_format')),
    // No default: neither effect is checked until the person picks one, and nothing is sent before.
    effect: z.custom<Effect>((x) => x === 'reduce_term' || x === 'reduce_payment', { error: t('loan_repayments.effect_required') }),
    penalty: z.string().refine(optionalAmount, t('loan_form.amount_format')),
    note: z.string().max(2000, t('investments_form.note_too_long')),
  });
}

export type RepaymentValues = z.infer<ReturnType<typeof repaymentSchema>>;

/** A server path to the field that owns it; any other engine refusal goes above the form. */
export const REPAYMENT_PATHS = {
  onDate: 'onDate', amount: 'amount', effect: 'effect', penalty: 'penalty', note: 'note',
} as const satisfies Record<string, keyof RepaymentValues>;

/**
 * A new repayment is dated today, its effect left unchecked, unless a simulation prefills it; a stored one opens as the
 * server wrote it, its penalty included.
 */
export function repaymentValuesOf(r?: LoanRepayment, prefill?: RepaymentPrefill | null): DefaultValues<RepaymentValues> {
  // The server writes only its own effects.
  if (r) return { onDate: r.onDate, amount: toFieldAmount(r.amount), effect: r.effect as Effect, penalty: toFieldAmount(r.penalty ?? ''), note: r.note ?? '' };
  if (prefill) return { onDate: prefill.onDate, amount: toFieldAmount(prefill.amount), effect: prefill.effect, penalty: toFieldAmount(prefill.penalty), note: '' };
  return { onDate: todayIso(), amount: '', penalty: '', note: '' };
}

/** The body: amounts as typed with a dot; an emptied penalty left out, which deletes it (3B2 design §7); `version` on a correction only. */
export function toRepaymentBody(v: RepaymentValues, version?: number): components['schemas']['LoanRepaymentBody'] {
  return {
    onDate: v.onDate, amount: toWireAmount(v.amount), effect: v.effect,
    ...(v.penalty.trim() ? { penalty: toWireAmount(v.penalty) } : {}),
    ...(v.note.trim() ? { note: v.note } : {}),
    ...(version === undefined ? {} : { version }),
  };
}

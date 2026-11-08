import type { TFunction } from 'i18next';
import { z } from 'zod';
import type { components } from '@/api/schema';
import { POSITIVE_AMOUNT, trimAmount, ISO_DATE, toFieldAmount, toWireAmount } from '@/lib/amount';
import { todayIso } from '@/lib/today';
import type { IncomeOneOff } from '../api';
import '@/locales/investments';
import '@/locales/forms';

/**
 * Spec §4: a date not after `last` (today, as the Core refuses a later one; an ended source's end, issue #22), a positive
 * amount, a note of at most 2000 characters.
 */
export function oneOffSchema(t: TFunction, last: string, ended = false) {
  return z.object({
    occurredOn: z.string().regex(ISO_DATE, t('edit.required'))
      .refine((d) => d <= last, ended ? t('field_error.income_one_off_after_end') : t('field_error.core_date_in_future')),
    amount: z.string().trim().regex(POSITIVE_AMOUNT, t('investments_form.positive_amount_format')),
    taxableAmount: z.string().refine((v) => v.trim() === '' || POSITIVE_AMOUNT.test(v.trim()), t('investments_form.positive_amount_format')),
    note: z.string().max(2000, t('investments_form.note_too_long')),
  });
}

export type OneOffValues = z.infer<ReturnType<typeof oneOffSchema>>;

/** A server path to the field that owns it. */
export const ONE_OFF_PATHS = {
  occurredOn: 'occurredOn', amount: 'amount', 'amount.amount': 'amount', 'amount.currency': 'amount', taxableAmount: 'taxableAmount',
  'taxableAmount.amount': 'taxableAmount', 'taxableAmount.currency': 'taxableAmount', note: 'note',
} as const satisfies Record<string, keyof OneOffValues>;

/** The Core's fact gate names no field: its refusals go to the only field they can concern. */
export const ONE_OFF_FALLBACK = {
  'core.date_in_future': 'occurredOn', 'core.amount_not_positive': 'amount', 'core.currency_mismatch': 'amount', 'core.invalid_fact': 'amount',
} as const satisfies Record<string, keyof OneOffValues>;

export function oneOffValuesOf(f?: IncomeOneOff, on = todayIso()): OneOffValues {
  return f ? { occurredOn: f.occurredOn, amount: toFieldAmount(trimAmount(f.amount.amount)), taxableAmount: f.taxableAmount ? toFieldAmount(trimAmount(f.taxableAmount.amount)) : '', note: f.note ?? '' }
    : { occurredOn: on, amount: '', taxableAmount: '', note: '' };
}

export function toWireOneOff(v: OneOffValues, currency: string): components['schemas']['IncomeOneOffBody'] {
  return { occurredOn: v.occurredOn, amount: { amount: toWireAmount(v.amount), currency },
    ...(v.taxableAmount.trim() ? { taxableAmount: { amount: toWireAmount(v.taxableAmount), currency } } : {}), ...(v.note.trim() ? { note: v.note } : {}) };
}

import type { TFunction } from 'i18next';
import { z } from 'zod';
import type { components } from '@/api/schema';
import { POSITIVE_AMOUNT, trimAmount } from '../../investments/entries/entries';
import { ISO_DATE, toWireAmount } from '../../subscriptions/edits/amount';
import { todayIso } from '../../subscriptions/today';
import type { IncomeOneOff } from '../api';

/** Spec §4: a date not after today (the Core refuses one), a positive amount, a note of at most 2000 characters. */
export function oneOffSchema(t: TFunction, today: string) {
  return z.object({
    occurredOn: z.string().regex(ISO_DATE, t('edit.required')).refine((d) => d <= today, t('field_error.core_date_in_future')),
    amount: z.string().trim().regex(POSITIVE_AMOUNT, t('investments_form.positive_amount_format')),
    note: z.string().max(2000, t('investments_form.note_too_long')),
  });
}

export type OneOffValues = z.infer<ReturnType<typeof oneOffSchema>>;

/** A server path to the field that owns it. */
export const ONE_OFF_PATHS = {
  occurredOn: 'occurredOn', amount: 'amount', 'amount.amount': 'amount', 'amount.currency': 'amount', note: 'note',
} as const satisfies Record<string, keyof OneOffValues>;

/** The Core's fact gate names no field: its refusals go to the only field they can concern. */
export const ONE_OFF_FALLBACK = {
  'core.date_in_future': 'occurredOn', 'core.amount_not_positive': 'amount', 'core.currency_mismatch': 'amount', 'core.invalid_fact': 'amount',
} as const satisfies Record<string, keyof OneOffValues>;

export function oneOffValuesOf(f?: IncomeOneOff): OneOffValues {
  return f ? { occurredOn: f.occurredOn, amount: trimAmount(f.amount.amount), note: f.note ?? '' } : { occurredOn: todayIso(), amount: '', note: '' };
}

export function toWireOneOff(v: OneOffValues, currency: string): components['schemas']['IncomeOneOffBody'] {
  return { occurredOn: v.occurredOn, amount: { amount: toWireAmount(v.amount), currency }, ...(v.note.trim() ? { note: v.note } : {}) };
}

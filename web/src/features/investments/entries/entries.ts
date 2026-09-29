import type { TFunction } from 'i18next';
import { z } from 'zod';
import { AMOUNT, ISO_DATE } from '../../subscriptions/edits/amount';
import { FLOW_KINDS } from '../format';

/** A value may be negative (an overdrawn current account); a flow never is — its kind gives the direction. */
export const SIGNED_AMOUNT = /^-?\d+([.,]\d{1,4})?$/;

export const valueSchema = (t: TFunction) => z.object({
  asOf: z.string().regex(ISO_DATE, t('edit.required')),
  amount: z.string().regex(SIGNED_AMOUNT, t('investments_form.amount_format')),
  note: z.string().max(2000, t('investments_form.note_too_long')),
});

export const flowSchema = (t: TFunction) => z.object({
  occurredOn: z.string().regex(ISO_DATE, t('edit.required')),
  kind: z.enum(FLOW_KINDS),
  amount: z.string().regex(AMOUNT, t('investments_form.positive_amount_format')),
  note: z.string().max(2000, t('investments_form.note_too_long')),
});

export type ValueFormValues = z.infer<ReturnType<typeof valueSchema>>;
export type FlowFormValues = z.infer<ReturnType<typeof flowSchema>>;

/** Trailing zeros of the decimals only, string trimming — never arithmetic: `25000.0000` → `25000`, `12.50` → `12.5`. */
export function trimAmount(amount: string): string {
  return amount.includes('.') ? amount.replace(/0+$/, '').replace(/\.$/, '') : amount;
}

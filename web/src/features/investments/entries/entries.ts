import type { TFunction } from 'i18next';
import { z } from 'zod';
import { pastDate } from '@/components/form/pastDate';
import { POSITIVE_AMOUNT, SIGNED_AMOUNT } from '@/lib/amount';
import { FLOW_KINDS } from '../format';
import '@/locales/investments';
import '@/locales/forms';

export const valueSchema = (t: TFunction) => z.object({
  asOf: pastDate(t),
  amount: z.string().regex(SIGNED_AMOUNT, t('investments_form.amount_format')),
  note: z.string().max(2000, t('investments_form.note_too_long')),
});

export const flowSchema = (t: TFunction) => z.object({
  occurredOn: pastDate(t),
  kind: z.enum(FLOW_KINDS),
  nature: z.string(),
  amount: z.string().regex(POSITIVE_AMOUNT, t('investments_form.positive_amount_format')),
  note: z.string().max(2000, t('investments_form.note_too_long')),
});

export type ValueFormValues = z.infer<ReturnType<typeof valueSchema>>;
export type FlowFormValues = z.infer<ReturnType<typeof flowSchema>>;

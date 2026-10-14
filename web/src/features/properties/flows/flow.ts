import type { TFunction } from 'i18next';
import { z } from 'zod';
import type { components } from '@/api/schema';
import { LOAN_AMOUNT } from '../../loans/new/newLoan';
import { ISO_DATE, toWireAmount } from '../../subscriptions/edits/amount';
import { todayIso } from '../../subscriptions/today';
import type { PropertyFlow } from '../api';
import { FLOW_KINDS } from '../format';

// The server's bounds (spec 3C1 §3): dated on or before today, positive, two decimals; the category is checked by it.
export function flowSchema(t: TFunction) {
  return z.object({
    kind: z.enum(FLOW_KINDS),
    category: z.string().min(1, t('edit.required')),
    occurredOn: z.string().regex(ISO_DATE, t('edit.required')).refine((v) => v <= todayIso(), t('field_error.core_date_in_future')),
    amount: z.string().trim().regex(LOAN_AMOUNT, t('loan_form.amount_format')),
    note: z.string().max(2000, t('investments_form.note_too_long')),
  });
}

export type FlowValues = z.infer<ReturnType<typeof flowSchema>>;

/** A server path to the field that owns it: the body's names are the form's. */
export const FLOW_PATHS = { kind: 'kind', category: 'category', occurredOn: 'occurredOn', amount: 'amount', note: 'note' } as const satisfies Record<string, keyof FlowValues>;

/** A new flow: an expense for works, today (precision 6); a stored one as the server wrote it. */
export function flowValuesOf(f?: PropertyFlow): FlowValues {
  // The server writes only its own kinds.
  return f
    ? { kind: f.kind as FlowValues['kind'], category: f.category ?? '', occurredOn: f.occurredOn, amount: f.amount, note: f.note ?? '' }
    : { kind: 'expense', category: 'works', occurredOn: todayIso(), amount: '', note: '' };
}

/** The body, the same for an addition and a correction: the amount as typed with a dot, an empty note left out. */
export function toFlowBody(v: FlowValues): components['schemas']['PropertyFlowBody'] {
  return {
    occurredOn: v.occurredOn, kind: v.kind, category: v.category, amount: toWireAmount(v.amount),
    ...(v.note.trim() ? { note: v.note } : {}),
  };
}

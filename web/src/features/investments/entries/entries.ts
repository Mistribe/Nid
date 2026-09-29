import type { TFunction } from 'i18next';
import type { FieldValues, Path, UseFormSetError } from 'react-hook-form';
import { z } from 'zod';
import { toProblem } from '@/api/errors';
import { AMOUNT, ISO_DATE } from '../../subscriptions/edits/amount';
import { fieldErrorsOf, messageFor } from '../../subscriptions/fieldErrors';
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

/** What `useSubmitErrors` gives back — named here so `placeFieldError` doesn't need to import the hook itself. */
type ErrorPlacement = { onError: (error: unknown) => void; clear: () => void };

/**
 * A failed value/flow write, placed on its field (1E2a design §2.1's rule, shared by `ValueForm` and `FlowForm`):
 * the server's `errors[]` first; failing that, a pathless Core refusal (Task 3: `core.date_in_future`,
 * `core.currency_mismatch`, `core.invalid_fact` carry no `errors[]`) attributed to the one field `fallback` says it
 * can concern; anything else is `useSubmitErrors`' own page-level `ProblemMessage`.
 */
export function placeFieldError<T extends FieldValues>(
  error: unknown, t: TFunction, setError: UseFormSetError<T>, errors: ErrorPlacement, fallback: Partial<Record<string, Path<T>>>,
): void {
  const problem = toProblem(error);
  const known = fieldErrorsOf(error);
  const field = known.length === 0 ? fallback[problem.code ?? ''] : undefined;
  if (field) {
    setError(field, { type: 'server', message: messageFor(t, { path: field, code: problem.code ?? '', message: problem.title ?? '' }) });
    errors.clear();
  } else {
    errors.onError(error);
  }
}

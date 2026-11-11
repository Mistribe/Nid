import type { TFunction } from 'i18next';
import { z } from 'zod';
import { checkOwnership, ownershipShape, ownershipToWire, type OwnershipValues } from '@/components/ownership/ownership';
import { CURRENCY, ISO_DATE, toWireAmount, POSITIVE_AMOUNT, SIGNED_AMOUNT } from '@/lib/amount';
import { todayIso } from '@/lib/today';
import { CADENCES, KINDS, REGULATED } from './format';
import '@/locales/investments';
import '@/locales/forms';

export function newEnvelopeSchema(t: TFunction) {
  const required = t('edit.required');
  return z.object({
    displayName: z.string().trim().min(1, required).max(100, t('investments_form.name_too_long')),
    kind: z.enum(KINDS),
    currency: z.string().trim().toUpperCase().regex(CURRENCY, t('field_error.request_currency')),
    institution: z.string().max(100, t('investments_form.institution_too_long')),
    accountHint: z.string().max(4, t('investments_form.account_hint_too_long')),
    valuationCadence: z.enum(CADENCES),
    regulated: z.enum(REGULATED),
    ...ownershipShape(t),
    withOpening: z.boolean(),
    openingOn: z.string(),
    openingAmount: z.string(),
    firstValueOn: z.string(),
    firstValueAmount: z.string(),
  }).superRefine((v, ctx) => {
    checkOwnership(v, ctx, t);
    const date = (on: string, path: string) => {
      if (!ISO_DATE.test(on)) ctx.addIssue({ code: 'custom', path: [path], message: required });
      else if (on > todayIso()) ctx.addIssue({ code: 'custom', path: [path], message: t('field_error.core_date_in_future') });
    };
    // The opening (a flow) must be positive, like any other flow (finding 3); the first value may be negative (an
    // overdrawn current account, finding 10) — it stays a value, not a flow.
    const openingAmount = (text: string, path: string) => { if (!POSITIVE_AMOUNT.test(text)) ctx.addIssue({ code: 'custom', path: [path], message: t('investments_form.positive_amount_format') }); };
    const valueAmount = (text: string, path: string) => { if (!SIGNED_AMOUNT.test(text)) ctx.addIssue({ code: 'custom', path: [path], message: t('investments_form.amount_format') }); };
    if (v.withOpening) { date(v.openingOn, 'openingOn'); openingAmount(v.openingAmount, 'openingAmount'); }
    if (hasFirstValue(v)) { date(v.firstValueOn, 'firstValueOn'); valueAmount(v.firstValueAmount, 'firstValueAmount'); }
  });
}

/** « Combien y a-t-il dessus aujourd'hui ? » is optional: a first value goes out once its amount is typed. */
export const hasFirstValue = (v: { firstValueAmount: string }) => v.firstValueAmount.trim() !== '';

export type NewEnvelopeValues = z.infer<ReturnType<typeof newEnvelopeSchema>> & OwnershipValues;

/** A server body path (1E2a design §2.1) to the field that owns it, for `useSubmitErrors`. */
export const NEW_ENVELOPE_PATHS: Record<string, string> = {
  displayName: 'displayName', kind: 'kind', currency: 'currency', institution: 'institution', accountHint: 'accountHint',
  valuationCadence: 'valuationCadence', regulated: 'regulated',
  'opening.occurredOn': 'openingOn', 'opening.amount': 'openingAmount',
  'firstValuation.asOf': 'firstValueOn', 'firstValuation.amount': 'firstValueAmount',
};

export function toCreateBody(v: NewEnvelopeValues) {
  const money = (text: string) => ({ amount: toWireAmount(text), currency: v.currency });
  return {
    displayName: v.displayName, kind: v.kind, currency: v.currency, valuationCadence: v.valuationCadence,
    institution: v.institution.trim() || undefined, accountHint: v.accountHint.trim() || undefined,
    regulated: v.kind === 'savings' && v.regulated !== 'none' ? v.regulated : undefined,
    ...ownershipToWire(v),
    ...(v.withOpening ? { opening: { occurredOn: v.openingOn, amount: money(v.openingAmount) } } : {}),
    ...(hasFirstValue(v) ? { firstValuation: { asOf: v.firstValueOn, amount: money(v.firstValueAmount) } } : {}),
  };
}

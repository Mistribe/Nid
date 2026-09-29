import type { TFunction } from 'i18next';
import { z } from 'zod';
import { checkOwnership, ownershipShape, ownershipToWire, type OwnershipValues } from '@/components/ownership/ownership';
import { AMOUNT, CURRENCY, ISO_DATE, toWireAmount } from '../subscriptions/edits/amount';
import { CADENCES, KINDS } from './format';

export function newEnvelopeSchema(t: TFunction) {
  const required = t('edit.required');
  return z.object({
    displayName: z.string().trim().min(1, required).max(100, t('investments_form.name_too_long')),
    kind: z.enum(KINDS),
    currency: z.string().trim().toUpperCase().regex(CURRENCY, t('field_error.request_currency')),
    institution: z.string().max(100, t('investments_form.institution_too_long')),
    accountHint: z.string().max(4, t('investments_form.account_hint_too_long')),
    valuationCadence: z.enum(CADENCES),
    ...ownershipShape(t),
    withOpening: z.boolean(),
    openingOn: z.string(),
    openingAmount: z.string(),
    withFirstValue: z.boolean(),
    firstValueOn: z.string(),
    firstValueAmount: z.string(),
  }).superRefine((v, ctx) => {
    checkOwnership(v, ctx, t);
    const date = (on: string, path: string) => { if (!ISO_DATE.test(on)) ctx.addIssue({ code: 'custom', path: [path], message: required }); };
    const amount = (text: string, path: string) => { if (!AMOUNT.test(text)) ctx.addIssue({ code: 'custom', path: [path], message: t('investments_form.amount_format') }); };
    if (v.withOpening) { date(v.openingOn, 'openingOn'); amount(v.openingAmount, 'openingAmount'); }
    if (v.withFirstValue) { date(v.firstValueOn, 'firstValueOn'); amount(v.firstValueAmount, 'firstValueAmount'); }
  });
}

export type NewEnvelopeValues = z.infer<ReturnType<typeof newEnvelopeSchema>> & OwnershipValues;

/** A server body path (1E2a design §2.1) to the field that owns it, for `useSubmitErrors`. */
export const NEW_ENVELOPE_PATHS: Record<string, string> = {
  displayName: 'displayName', kind: 'kind', currency: 'currency', institution: 'institution', accountHint: 'accountHint',
  valuationCadence: 'valuationCadence',
  'opening.occurredOn': 'openingOn', 'opening.amount': 'openingAmount',
  'firstValuation.asOf': 'firstValueOn', 'firstValuation.amount': 'firstValueAmount',
};

export function toCreateBody(v: NewEnvelopeValues) {
  const money = (text: string) => ({ amount: toWireAmount(text), currency: v.currency });
  return {
    displayName: v.displayName, kind: v.kind, currency: v.currency, valuationCadence: v.valuationCadence,
    institution: v.institution.trim() || undefined, accountHint: v.accountHint.trim() || undefined,
    ...ownershipToWire(v),
    ...(v.withOpening ? { opening: { occurredOn: v.openingOn, amount: money(v.openingAmount) } } : {}),
    ...(v.withFirstValue ? { firstValuation: { asOf: v.firstValueOn, amount: money(v.firstValueAmount) } } : {}),
  };
}

import type { TFunction } from 'i18next';
import { z } from 'zod';
import type { components } from '@/api/schema';
import { LOAN_AMOUNT } from '../../loans/new/newLoan';
import { ISO_DATE, toWireAmount } from '../../subscriptions/edits/amount';
import { todayIso } from '../../subscriptions/today';
import type { PropertyTenancy } from '../api';
import { FEE_AMOUNT } from '../new/newProperty';

const optionalAmount = (v: string) => v.trim() === '' || LOAN_AMOUNT.test(v.trim());

/**
 * The server's bounds (spec 3C2 §2): a label of 1 to 100 characters, a positive rent, charges zero or more, a deposit
 * positive when given, an end after the start. The purchase date and the overlap are the server's, answered at `startsOn`.
 */
export function tenancySchema(t: TFunction) {
  return z.object({
    label: z.string().trim().min(1, t('edit.required')).max(100, t('investments_form.name_too_long')),
    startsOn: z.string().regex(ISO_DATE, t('edit.required')),
    endsOn: z.string(),
    rentMonthly: z.string().trim().regex(LOAN_AMOUNT, t('loan_form.amount_format')),
    chargesMonthly: z.string().trim().regex(FEE_AMOUNT, t('property_form.fee_format')),
    deposit: z.string().refine(optionalAmount, t('loan_form.amount_format')),
    note: z.string().max(2000, t('investments_form.note_too_long')),
  }).superRefine((v, ctx) => {
    if (v.endsOn && v.endsOn <= v.startsOn) ctx.addIssue({ code: 'custom', path: ['endsOn'], message: t('field_error.subscriptions_phase_range') });
  });
}

export type TenancyValues = z.infer<ReturnType<typeof tenancySchema>>;

/** A server path to the field that owns it: the body's names are the form's (the overlap answers at `startsOn`). */
export const TENANCY_PATHS = {
  label: 'label', startsOn: 'startsOn', endsOn: 'endsOn', rentMonthly: 'rentMonthly', chargesMonthly: 'chargesMonthly', deposit: 'deposit',
  note: 'note',
} as const satisfies Record<string, keyof TenancyValues>;

/** A new tenancy from today, charges 0 (spec §3); a stored one as the server wrote it, an absent end, deposit or note empty. */
export function tenancyValuesOf(x?: PropertyTenancy): TenancyValues {
  return x
    ? {
      label: x.label, startsOn: x.startsOn, endsOn: x.endsOn ?? '', rentMonthly: x.rentMonthly, chargesMonthly: x.chargesMonthly,
      deposit: x.deposit ?? '', note: x.note ?? '',
    }
    : { label: '', startsOn: todayIso(), endsOn: '', rentMonthly: '', chargesMonthly: '0', deposit: '', note: '' };
}

/**
 * The whole tenancy — a `PUT` replaces every value, so an emptied end, deposit or note is left out and so removed; amounts
 * as typed with a dot; `version` on a correction only.
 */
export function toTenancyBody(v: TenancyValues, version?: number): components['schemas']['PropertyTenancyBody'] {
  return {
    label: v.label.trim(), startsOn: v.startsOn, rentMonthly: toWireAmount(v.rentMonthly), chargesMonthly: toWireAmount(v.chargesMonthly),
    ...(v.endsOn ? { endsOn: v.endsOn } : {}),
    ...(v.deposit.trim() ? { deposit: toWireAmount(v.deposit) } : {}),
    ...(v.note.trim() ? { note: v.note } : {}),
    ...(version === undefined ? {} : { version }),
  };
}

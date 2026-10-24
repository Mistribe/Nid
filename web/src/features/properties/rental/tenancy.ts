import type { TFunction } from 'i18next';
import { z } from 'zod';
import type { components } from '@/api/schema';
import { LOAN_AMOUNT } from '../../loans/new/loanForm';
import { ISO_DATE, toWireAmount } from '../../subscriptions/edits/amount';
import { todayIso } from '../../subscriptions/today';
import type { PropertyTenancy } from '../api';
import { FEE_AMOUNT } from '../new/propertyForm';
import '@/locales/investments';
import '@/locales/loans';
import '@/locales/properties';
import '@/locales/forms';

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
    indexed: z.boolean(),
    // « 1 » to « 4 », or empty while the tenancy is not indexed.
    irlQuarter: z.string(),
    // Rental receipts spec §2: optional, for receipts and letters; 200 characters, addresses 400.
    tenantNames: z.string().max(200, t('property_rental.identity_max')),
    landlordName: z.string().max(200, t('property_rental.identity_max')),
    landlordAddress: z.string().max(400, t('property_rental.address_max')),
    // `yyyy-mm`, or empty: the server finds the first rent tracked.
    rentsTrackedFrom: z.string(),
  }).superRefine((v, ctx) => {
    if (v.indexed && v.irlQuarter === '') ctx.addIssue({ code: 'custom', path: ['irlQuarter'], message: t('edit.required') });
    if (v.endsOn && v.endsOn <= v.startsOn) ctx.addIssue({ code: 'custom', path: ['endsOn'], message: t('field_error.subscriptions_phase_range') });
  });
}

export type TenancyValues = z.infer<ReturnType<typeof tenancySchema>>;

/** A server path to the field that owns it: the body's names are the form's (the overlap answers at `startsOn`). */
export const TENANCY_PATHS = {
  label: 'label', startsOn: 'startsOn', endsOn: 'endsOn', rentMonthly: 'rentMonthly', chargesMonthly: 'chargesMonthly', deposit: 'deposit',
  note: 'note', indexed: 'indexed', irlReferenceQuarter: 'irlQuarter',
  tenantNames: 'tenantNames', landlordName: 'landlordName', landlordAddress: 'landlordAddress', rentsTrackedFrom: 'rentsTrackedFrom',
} as const satisfies Record<string, keyof TenancyValues>;

/** A new tenancy from today, charges 0 (spec §3); a stored one as the server wrote it, an absent end, deposit or note empty. */
export function tenancyValuesOf(x?: PropertyTenancy): TenancyValues {
  return x
    ? {
      label: x.label, startsOn: x.startsOn, endsOn: x.endsOn ?? '', rentMonthly: x.rentMonthly, chargesMonthly: x.chargesMonthly,
      deposit: x.deposit ?? '', note: x.note ?? '', indexed: x.indexed, irlQuarter: x.irlReferenceQuarter == null ? '' : String(x.irlReferenceQuarter),
      tenantNames: x.tenantNames ?? '', landlordName: x.landlordName ?? '', landlordAddress: x.landlordAddress ?? '',
      rentsTrackedFrom: x.rentsTrackedFrom ?? '',
    }
    : {
      label: '', startsOn: todayIso(), endsOn: '', rentMonthly: '', chargesMonthly: '0', deposit: '', note: '', indexed: false, irlQuarter: '',
      tenantNames: '', landlordName: '', landlordAddress: '', rentsTrackedFrom: '',
    };
}

/**
 * The whole tenancy — a `PUT` replaces every value, so an emptied end, deposit or note is left out and so removed; amounts
 * as typed with a dot; `version` on a correction only. The identity fields and the month rents are tracked from are tri-state:
 * a correction always sends them, an emptied one as `""` (clears it); a creation sends only those given.
 */
export function toTenancyBody(v: TenancyValues, version?: number): components['schemas']['PropertyTenancyBody'] {
  return {
    label: v.label.trim(), startsOn: v.startsOn, rentMonthly: toWireAmount(v.rentMonthly), chargesMonthly: toWireAmount(v.chargesMonthly),
    ...(v.endsOn ? { endsOn: v.endsOn } : {}),
    ...(v.deposit.trim() ? { deposit: toWireAmount(v.deposit) } : {}),
    ...(v.note.trim() ? { note: v.note } : {}),
    indexed: v.indexed,
    ...(v.indexed ? { irlReferenceQuarter: Number(v.irlQuarter) } : {}),
    ...identity(v, version !== undefined),
    ...(v.rentsTrackedFrom || version !== undefined ? { rentsTrackedFrom: v.rentsTrackedFrom } : {}),
    ...(version === undefined ? {} : { version }),
  };
}

/**
 * The months a lease's rents can be tracked from, newest first: its start's month to the current one, `yyyy-mm` counted as text;
 * a start still being typed counts as today.
 */
export function trackedMonths(startsOn: string, today: string): string[] {
  const out: string[] = [];
  const [fromYear, fromMonth] = (ISO_DATE.test(startsOn) ? startsOn : today).split('-').map(Number) as [number, number];
  let [year, month] = today.split('-').map(Number) as [number, number];
  while (year > fromYear || (year === fromYear && month >= fromMonth)) {
    out.push(`${year}-${String(month).padStart(2, '0')}`);
    [year, month] = month === 1 ? [year - 1, 12] : [year, month - 1];
  }
  return out;
}

/** The first start of the revision chain a link belongs to: the month rents are tracked from is the whole lease's. */
export function chainStart(tenancies: readonly PropertyTenancy[], link: PropertyTenancy): string {
  let first = link;
  for (let previous = tenancies.find((x) => x.id === first.revisedFrom); previous; previous = tenancies.find((x) => x.id === first.revisedFrom)) first = previous;
  return first.startsOn;
}

function identity(v: TenancyValues, correcting: boolean) {
  const out: Pick<components['schemas']['PropertyTenancyBody'], 'tenantNames' | 'landlordName' | 'landlordAddress'> = {};
  for (const key of ['tenantNames', 'landlordName', 'landlordAddress'] as const) {
    const text = v[key].trim();
    if (text || correcting) out[key] = text;
  }
  return out;
}

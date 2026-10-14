import type { TFunction } from 'i18next';
import type { Path } from 'react-hook-form';
import { z } from 'zod';
import type { components } from '@/api/schema';
import { checkOwnership, ownershipShape, ownershipToWire } from '@/components/ownership/ownership';
import { LOAN_AMOUNT } from '../../loans/new/newLoan';
import { CURRENCY, ISO_DATE, toWireAmount } from '../../subscriptions/edits/amount';
import { todayIso } from '../../subscriptions/today';
import { CADENCES, PROPERTY_TYPES } from '../format';

/** A fee or another acquisition cost as typed (spec 3C1 §2): zero allowed, at most two decimals, below 10¹², a comma or a dot. */
export const FEE_AMOUNT = /^\d{1,12}(?:[.,]\d{1,2})?$/;

const optionalAmount = (v: string) => v.trim() === '' || LOAN_AMOUNT.test(v.trim());
// A count of days, not an amount: a plain integer, 1 to 3 650 (spec 3C1 §2).
const optionalDays = (v: string) => v.trim() === '' || (/^\d{1,4}$/.test(v.trim()) && Number(v.trim()) >= 1 && Number(v.trim()) <= 3650);

/** The property's own fields (3D1 design §5–§6), the creation's and the settings' alike, each as typed; the checks mirror the server's. */
export function propertyFieldsShape(t: TFunction) {
  return {
    name: z.string().trim().min(1, t('edit.required')).max(100, t('investments_form.name_too_long')),
    type: z.enum(PROPERTY_TYPES),
    // ISO dates compare as strings; today is earlier than 2200, so « on or before today » is the upper bound.
    purchaseDate: z.string().regex(ISO_DATE, t('edit.required'))
      .refine((v) => v >= '1900-01-01', t('property_form.date_range'))
      .refine((v) => v <= todayIso(), t('field_error.core_date_in_future')),
    purchasePrice: z.string().trim().regex(LOAN_AMOUNT, t('loan_form.amount_format')),
    notaryFees: z.string().trim().regex(FEE_AMOUNT, t('property_form.fee_format')),
    agencyFees: z.string().trim().regex(FEE_AMOUNT, t('property_form.fee_format')),
    otherAcquisitionCosts: z.string().trim().regex(FEE_AMOUNT, t('property_form.fee_format')),
    surfaceM2: z.string().refine(optionalAmount, t('property_form.surface_format')),
    locationLabel: z.string().max(100, t('property_form.location_too_long')),
    valuationCadence: z.enum(CADENCES),
    staleAfterDays: z.string().refine(optionalDays, t('property_form.stale_range')),
    note: z.string().max(2000, t('investments_form.note_too_long')),
  };
}

/** The settings' schema (3D1 design §6): the property's own fields. */
export function propertyFieldsSchema(t: TFunction) {
  return z.object(propertyFieldsShape(t));
}

export type PropertyFieldsValues = z.infer<ReturnType<typeof propertyFieldsSchema>>;

/** Spec §5: the property's identity, then its own fields. */
export function newPropertySchema(t: TFunction) {
  return z.object({
    ...propertyFieldsShape(t),
    currency: z.string().trim().toUpperCase().regex(CURRENCY, t('field_error.request_currency')),
    // No default (spec §5): the person answers, or nothing is sent.
    // Not aborting: an aborting issue would skip the superRefine below and hide its checks until a second submit.
    visibility: z.custom<'shared' | 'private'>((x) => x === 'shared' || x === 'private', { error: t('loan_form.visibility_required'), abort: false }),
    owners: ownershipShape(t).owners,
  }).superRefine((v, ctx) => checkOwnership(v, ctx, t));
}

export type NewPropertyValues = z.infer<ReturnType<typeof newPropertySchema>>;

/** The one creation request (spec §5): amounts as typed with a dot, empty optional fields left out; no rental start (3D2). */
export function toCreateBody(v: NewPropertyValues): components['schemas']['CreatePropertyBody'] {
  const { visibility, owners } = ownershipToWire(v);
  return {
    name: v.name.trim(), currency: v.currency, visibility, owners, type: v.type, purchaseDate: v.purchaseDate,
    purchasePrice: toWireAmount(v.purchasePrice), notaryFees: toWireAmount(v.notaryFees), agencyFees: toWireAmount(v.agencyFees),
    otherAcquisitionCosts: toWireAmount(v.otherAcquisitionCosts), valuationCadence: v.valuationCadence,
    ...(v.surfaceM2.trim() ? { surfaceM2: toWireAmount(v.surfaceM2) } : {}),
    ...(v.locationLabel.trim() ? { locationLabel: v.locationLabel.trim() } : {}),
    ...(v.staleAfterDays.trim() ? { staleAfterDays: Number(v.staleAfterDays.trim()) } : {}),
    ...(v.note.trim() ? { note: v.note } : {}),
  };
}

const FIELDS = [
  'name', 'type', 'purchaseDate', 'purchasePrice', 'notaryFees', 'agencyFees', 'otherAcquisitionCosts', 'surfaceM2', 'locationLabel',
  'valuationCadence', 'staleAfterDays', 'note',
] as const satisfies readonly (keyof PropertyFieldsValues)[];

/** A server path of the property's own fields to the field that owns it: the body's names are the form's. */
export function propertyFieldPath(path: string): keyof PropertyFieldsValues | undefined {
  return FIELDS.find((f) => f === path);
}

/** A creation path: the identity's (an owner's path lands on the owners block), or one of the property's fields. */
export function newPropertyPath(path: string): Path<NewPropertyValues> | undefined {
  if (path === 'currency' || path === 'visibility') return path;
  if (path === 'owners' || path.startsWith('owners[')) return 'owners';
  return propertyFieldPath(path);
}

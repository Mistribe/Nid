import type { TFunction } from 'i18next';
import type { Path } from 'react-hook-form';
import { z } from 'zod';
import { ASSET_KINDS, type AssetView, type CreateAssetBody, type EditAssetBody } from '@/api/assets';
import { checkOwnership, ownershipShape, ownershipToWire } from '@/components/ownership/ownership';
import { CURRENCY, ISO_DATE, POSITIVE_AMOUNT, toFieldAmount, toWireAmount } from '@/lib/amount';
import { todayIso } from '@/lib/today';
import '@/locales/assets';
import '@/locales/investments';
import '@/locales/loans';
import '@/locales/forms';

const optional = (ok: (text: string) => boolean) => (v: string) => v.trim() === '' || ok(v.trim());
// A weight is positive; a fineness is above 0 and at most 1 000 per mille, one decimal at most (the server's rule).
const WEIGHT = /^\d{1,9}(?:[.,]\d{1,3})?$/;
const FINENESS = /^\d{1,4}(?:[.,]\d)?$/;
const MILEAGE = /^\d{1,7}$/;
const FISCAL_HP = /^\d{1,2}$/;
const asNumber = (text: string) => Number(text.trim().replace(',', '.'));

/** The asset's own fields, as typed, the creation's and the edit's alike. */
function fieldsShape(t: TFunction) {
  return {
    name: z.string().trim().min(1, t('edit.required')).max(100, t('investments_form.name_too_long')),
    kind: z.enum(ASSET_KINDS),
    weightGrams: z.string(),
    fineness: z.string(),
    registeredOn: z.string(),
    mileageKm: z.string(),
    mileageOn: z.string(),
    fiscalHp: z.string(),
    energy: z.string(),
    note: z.string().max(2000, t('investments_form.note_too_long')),
  };
}

/** Weight and fineness only count for a precious metal: the fields are hidden (and unchecked) for any other kind. */
function checkMetal(v: { kind: string; weightGrams: string; fineness: string }, ctx: z.RefinementCtx, t: TFunction) {
  if (v.kind !== 'precious_metal') return;
  if (!optional((x) => WEIGHT.test(x) && asNumber(x) > 0)(v.weightGrams)) ctx.addIssue({ code: 'custom', path: ['weightGrams'], message: t('assets_form.weight_format') });
  if (!optional((x) => FINENESS.test(x) && asNumber(x) > 0 && asNumber(x) <= 1000)(v.fineness)) ctx.addIssue({ code: 'custom', path: ['fineness'], message: t('assets_form.fineness_format') });
}

const pastDate = (v: string) => ISO_DATE.test(v) && v >= '1900-01-01' && v <= todayIso();

/** The vehicle's details only count for a vehicle: the fields are hidden (and unchecked) for any other kind. */
export function checkVehicle(v: VehicleValues, ctx: z.RefinementCtx, t: TFunction) {
  if (v.kind !== 'vehicle') return;
  const issue = (path: string, message: string) => ctx.addIssue({ code: 'custom', path: [path], message });
  if (!optional(pastDate)(v.registeredOn)) issue('registeredOn', t('assets_form.date_past'));
  if (!optional((x) => MILEAGE.test(x))(v.mileageKm)) issue('mileageKm', t('assets_form.mileage_format'));
  else if (v.mileageKm.trim() !== '' && v.mileageOn === '') issue('mileageOn', t('assets_form.mileage_incomplete'));
  else if (v.mileageKm.trim() === '' && v.mileageOn !== '') issue('mileageKm', t('assets_form.mileage_incomplete'));
  if (!optional(pastDate)(v.mileageOn)) issue('mileageOn', t('assets_form.date_past'));
  if (!optional((x) => FISCAL_HP.test(x) && asNumber(x) >= 1)(v.fiscalHp)) issue('fiscalHp', t('assets_form.fiscal_hp_format'));
}
type VehicleValues = { kind: string; registeredOn: string; mileageKm: string; mileageOn: string; fiscalHp: string; energy: string };

const dated = (t: TFunction) => z.string().refine(optional((v) => ISO_DATE.test(v) && v >= '1900-01-01'), t('edit.required'))
  .refine((v) => v <= todayIso(), t('field_error.core_date_in_future'));
const amount = (t: TFunction) => z.string().refine(optional((v) => POSITIVE_AMOUNT.test(v)), t('loan_form.amount_format'));

/** Creation: the value is asked as one amount and one date, which the person says are the purchase or today's; the server insists on one. */
export function newAssetSchema(t: TFunction) {
  return z.object({
    ...fieldsShape(t),
    currency: z.string().trim().toUpperCase().regex(CURRENCY, t('field_error.request_currency')),
    mode: z.enum(['purchase', 'current']),
    amount: amount(t),
    valueOn: dated(t),
    visibility: z.custom<'shared' | 'private'>((x) => x === 'shared' || x === 'private', { error: t('loan_form.visibility_required'), abort: false }),
    owners: ownershipShape(t).owners,
    shareLevel: ownershipShape(t).shareLevel,
  }).superRefine((v, ctx) => {
    checkMetal(v, ctx, t); checkVehicle(v, ctx, t); checkOwnership(v, ctx, t);
    if (v.mode === 'purchase' && v.amount.trim() !== '' && v.valueOn === '') ctx.addIssue({ code: 'custom', path: ['valueOn'], message: t('assets_form.purchase_incomplete') });
  });
}
export type NewAssetValues = z.infer<ReturnType<typeof newAssetSchema>>;

/** Edit: the purchase stays editable, the value choice is not asked (the valuations tab holds the values). */
export function editAssetSchema(t: TFunction) {
  return z.object({ ...fieldsShape(t), purchasePrice: amount(t), purchasedOn: dated(t) }).superRefine((v, ctx) => { checkMetal(v, ctx, t); checkVehicle(v, ctx, t); });
}
export type EditAssetValues = z.infer<ReturnType<typeof editAssetSchema>>;
/** What the shared fields read from the context: both forms have them. */
export type AssetFieldsValues = EditAssetValues;

const metal = (v: { kind: string; weightGrams: string; fineness: string }) => v.kind === 'precious_metal' ? {
  ...(v.weightGrams.trim() ? { weightGrams: asNumber(v.weightGrams) } : {}),
  ...(v.fineness.trim() ? { fineness: asNumber(v.fineness) } : {}),
} : {};

const vehicle = (v: VehicleValues) => v.kind === 'vehicle' ? {
  ...(v.registeredOn ? { registeredOn: v.registeredOn } : {}),
  ...(v.mileageKm.trim() ? { mileageKm: asNumber(v.mileageKm), mileageOn: v.mileageOn } : {}),
  ...(v.fiscalHp.trim() ? { fiscalHp: asNumber(v.fiscalHp) } : {}),
  ...(v.energy ? { energy: v.energy } : {}),
} : {};

/** The creation request: empty optional fields are left out, so the server answers what is missing. */
export function toCreateBody(v: NewAssetValues): CreateAssetBody {
  const { owners, ...sharing } = ownershipToWire(v);
  const value = v.amount.trim() === '' ? {}
    : v.mode === 'purchase' ? { purchasePrice: toWireAmount(v.amount), ...(v.valueOn ? { purchasedOn: v.valueOn } : {}) }
      : { currentValue: toWireAmount(v.amount), currentValueOn: v.valueOn || todayIso() };
  return { name: v.name.trim(), kind: v.kind, currency: v.currency, ...sharing, owners, ...metal(v), ...vehicle(v), ...value, ...(v.note.trim() ? { note: v.note } : {}) };
}

/** The edit request replaces every field. */
export function toEditBody(v: EditAssetValues, version: number): EditAssetBody {
  return {
    name: v.name.trim(), kind: v.kind, version, ...metal(v), ...vehicle(v),
    ...(v.purchasePrice.trim() ? { purchasePrice: toWireAmount(v.purchasePrice) } : {}),
    ...(v.purchasedOn ? { purchasedOn: v.purchasedOn } : {}),
    ...(v.note.trim() ? { note: v.note } : {}),
  };
}

export function editDefaults(a: AssetView): EditAssetValues {
  return {
    name: a.name, kind: a.kind as EditAssetValues['kind'], note: a.note ?? '',
    weightGrams: a.weightGrams == null ? '' : toFieldAmount(String(a.weightGrams)), fineness: a.fineness == null ? '' : toFieldAmount(String(a.fineness)),
    registeredOn: a.registeredOn ?? '', mileageKm: a.mileageKm == null ? '' : String(a.mileageKm), mileageOn: a.mileageOn ?? '',
    fiscalHp: a.fiscalHp == null ? '' : String(a.fiscalHp), energy: a.energy ?? '',
    purchasePrice: a.purchasePrice ? toFieldAmount(a.purchasePrice.amount.replace(/\.00$/, '')) : '', purchasedOn: a.purchasedOn ?? '',
  };
}

const OWN = ['name', 'kind', 'weightGrams', 'fineness', 'registeredOn', 'mileageKm', 'mileageOn', 'fiscalHp', 'energy', 'note'] as const;

/** A server path to the field that owns it: the one value is the form's amount and its date. */
export function assetFieldPath(path: string): (typeof OWN)[number] | 'amount' | 'valueOn' | 'purchasePrice' | 'purchasedOn' | undefined {
  if (path === 'currentValue') return 'amount';
  if (path === 'currentValueOn') return 'valueOn';
  if (path === 'purchasePrice' || path === 'purchasedOn') return path;
  return OWN.find((f) => f === path);
}

/** A creation path: the purchase's names land on the value block, the identity's on themselves. */
export function newAssetPath(path: string): Path<NewAssetValues> | undefined {
  if (path === 'currency' || path === 'visibility') return path;
  if (path === 'owners' || path.startsWith('owners[')) return 'owners';
  if (path === 'purchasePrice') return 'amount';
  if (path === 'purchasedOn') return 'valueOn';
  const own = assetFieldPath(path);
  return own === 'purchasePrice' || own === 'purchasedOn' ? undefined : own;
}

export function editAssetPath(path: string): Path<EditAssetValues> | undefined {
  const own = assetFieldPath(path);
  return own === 'amount' || own === 'valueOn' ? undefined : own;
}

const REFUSALS: Record<string, string> = {
  'assets.value_required': 'assets_form.value_required', 'assets.purchase_incomplete': 'assets_form.purchase_incomplete',
  'assets.metal_only': 'assets_form.metal_only', 'assets.invalid_fineness': 'assets_form.invalid_fineness', 'assets.invalid_weight': 'assets_form.invalid_weight',
  'assets.vehicle_only': 'assets_form.vehicle_only', 'assets.mileage_incomplete': 'assets_form.mileage_incomplete',
  'assets.invalid_mileage': 'assets_form.mileage_format', 'assets.invalid_fiscal_hp': 'assets_form.fiscal_hp_format', 'assets.invalid_date': 'assets_form.date_past',
};
/** The server's refusal of a field in the screen's own words (its code), else the generic wording. */
export const refusalText = (t: TFunction, code: string): string | undefined => (REFUSALS[code] ? t(REFUSALS[code] as 'assets_form.value_required') : undefined);

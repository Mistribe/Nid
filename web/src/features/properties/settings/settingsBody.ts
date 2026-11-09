import type { TFunction } from 'i18next';
import type { Path } from 'react-hook-form';
import { z } from 'zod';
import type { components } from '@/api/schema';
import { checkEditOwnership, editOwnershipShape, ownershipChange, ownershipDefaults, ownershipPath } from '@/components/ownership/ownership';
import { toFieldAmount, toWireAmount } from '@/lib/amount';
import type { PropertyDetail } from '@/api/properties';
import { propertyFieldPath, propertyFieldsSchema } from '../new/propertyForm';

/** Only the fields that changed; an emptied optional field says so with its `clear*` flag. */
export type EditBody = Partial<components['schemas']['EditPropertyBody']>;

const AMOUNTS = ['purchasePrice', 'notaryFees', 'agencyFees', 'otherAcquisitionCosts'] as const;

/**
 * The settings' fields (3D2 design §5): the property's own, and its rental start — empty, or a date from the date input; #70: its
 * visibility, sharing level and owners, for its owners only.
 */
export function settingsSchema(t: TFunction) {
  // The energy class (DPE) is a letter A to G, or empty when unknown.
  return propertyFieldsSchema(t).extend({
    rentalStartsOn: z.string(), energyClass: z.string(), postalAddress: z.string().max(400, t('property_form.postal_address_max')),
    ...editOwnershipShape(t),
  }).superRefine((v, ctx) => checkEditOwnership(v, ctx, t));
}

export type SettingsValues = z.infer<ReturnType<typeof settingsSchema>>;

/** A server path to the field that owns it: the rental start (refused before the purchase), or one of the property's fields. */
export function settingsPath(path: string): Path<SettingsValues> | undefined {
  return path === 'rentalStartsOn' || path === 'energyClass' || path === 'postalAddress' ? path : propertyFieldPath(path) ?? ownershipPath(path);
}

/** The form's values for a property as the server wrote it: amounts as their strings, an absent optional field empty. */
export function settingsValuesOf(p: PropertyDetail): SettingsValues {
  // The server writes only its own types and cadences.
  return {
    name: p.name, type: p.type as SettingsValues['type'], purchaseDate: p.purchaseDate, purchasePrice: toFieldAmount(p.purchasePrice),
    notaryFees: toFieldAmount(p.notaryFees), agencyFees: toFieldAmount(p.agencyFees),
    otherAcquisitionCosts: toFieldAmount(p.otherAcquisitionCosts), surfaceM2: toFieldAmount(p.surfaceM2 ?? ''),
    locationLabel: p.locationLabel ?? '', valuationCadence: p.valuationCadence as SettingsValues['valuationCadence'],
    staleAfterDays: p.staleAfterDays == null ? '' : String(p.staleAfterDays), rentalStartsOn: p.rentalStartsOn ?? '', note: p.note ?? '', energyClass: p.energyClass ?? '',
    postalAddress: p.postalAddress ?? '',
    // #70: shown, and so sent, to its owners only.
    ...(p.canChangeOwners ? ownershipDefaults(p) : {}),
  };
}

/** Against what the form opened on; `null` when nothing changed, so the form says so instead of sending an empty patch. */
export function toEditBody(before: PropertyDetail, v: SettingsValues): EditBody | null {
  const was = settingsValuesOf(before);
  const body: EditBody = {};
  if (v.name.trim() !== was.name.trim()) body.name = v.name.trim();
  if (v.type !== was.type) body.type = v.type;
  if (v.purchaseDate !== was.purchaseDate) body.purchaseDate = v.purchaseDate;
  // An amount still showing the server's text is unchanged (precision 9); anything typed goes out with a dot.
  for (const field of AMOUNTS) if (v[field].trim() !== was[field]) body[field] = toWireAmount(v[field]);
  if (v.valuationCadence !== was.valuationCadence) body.valuationCadence = v.valuationCadence;
  const surface = v.surfaceM2.trim();
  if (surface !== was.surfaceM2) { if (surface === '') body.clearSurfaceM2 = true; else body.surfaceM2 = toWireAmount(surface); }
  const location = v.locationLabel.trim();
  if (location !== was.locationLabel.trim()) { if (location === '') body.clearLocationLabel = true; else body.locationLabel = location; }
  const days = v.staleAfterDays.trim();
  if (days !== was.staleAfterDays) { if (days === '') body.clearStaleAfterDays = true; else body.staleAfterDays = Number(days); }
  if (v.rentalStartsOn !== was.rentalStartsOn) {
    if (v.rentalStartsOn === '') body.clearRentalStartsOn = true; else body.rentalStartsOn = v.rentalStartsOn;
  }
  if (v.energyClass !== was.energyClass) { if (v.energyClass === '') body.clearEnergyClass = true; else body.energyClass = v.energyClass; }
  const address = v.postalAddress.trim();
  if (address !== was.postalAddress.trim()) { if (address === '') body.clearPostalAddress = true; else body.postalAddress = address; }
  if (v.note.trim() !== was.note.trim()) { if (v.note.trim() === '') body.clearNote = true; else body.note = v.note; }
  Object.assign(body, ownershipChange(before, v));
  return Object.keys(body).length === 0 ? null : body;
}

/** The wire body: the version the form opened on, and every `clear*` flag stated (the wire type requires them). */
export function toWireBody(body: EditBody, version: number): components['schemas']['EditPropertyBody'] {
  return {
    clearSurfaceM2: false, clearLocationLabel: false, clearStaleAfterDays: false, clearRentalStartsOn: false, clearNote: false, clearEnergyClass: false, clearPostalAddress: false,
    ...body, version,
  };
}

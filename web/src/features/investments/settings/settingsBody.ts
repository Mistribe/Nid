import type { components } from '@/api/schema';
import type { EnvelopeDetail } from '@/api/investments';
import { toWireAmount } from '@/lib/amount';
import { hasPremiumsBefore2017 } from '../format';

/** What the settings form holds: every field as the text typed, compared and sent trimmed. */
export type SettingsValues = {
  displayName: string; kind: string; institution: string; accountHint: string;
  valuationCadence: string; staleAfterDays: string; ucShareFloorPct: string; note: string;
  openedOn: string; premiumsBefore2017: string; regulated: string;
};

/** Only the fields that changed; an emptied optional field says so with its `clear*` flag. */
export type EditBody = Partial<components['schemas']['EditEnvelopeBody']>;

const OPTIONAL_TEXT = [
  ['institution', 'clearInstitution'],
  ['accountHint', 'clearAccountHint'],
  ['note', 'clearNote'],
  ['openedOn', 'clearOpenedOn'],
] as const;

/** `null` when nothing changed, so the form can say so instead of sending an empty patch. */
export function toEditBody(before: EnvelopeDetail, values: SettingsValues): EditBody | null {
  const body: EditBody = {};
  const name = values.displayName.trim();
  if (name !== before.displayName.trim()) body.displayName = name;
  if (values.kind !== before.kind) body.kind = values.kind;
  if (values.valuationCadence !== before.valuationCadence) body.valuationCadence = values.valuationCadence;

  for (const [field, clear] of OPTIONAL_TEXT) {
    const next = values[field].trim();
    const current = (before[field] ?? '').trim();
    if (next === current) continue;
    if (next === '') body[clear] = true; else body[field] = next;
  }

  // A count of days, not an amount: `Number` is fine here.
  const stale = values.staleAfterDays.trim();
  if (stale === '') {
    if (before.staleAfterDays != null) body.clearStaleAfterDays = true;
  } else if (Number(stale) !== before.staleAfterDays) {
    body.staleAfterDays = Number(stale);
  }

  // A percentage, not an amount: `Number` is fine here too.
  const floor = values.ucShareFloorPct.trim().replace(',', '.');
  if (floor === '') {
    if (before.ucShareFloorPct != null) body.clearUcShareFloorPct = true;
  } else if (Number(floor) !== before.ucShareFloorPct) {
    body.ucShareFloorPct = Number(floor);
  }

  // Only a life insurance or a tontine has it, and the server drops it when the kind changes: never sent with a kind change.
  if (hasPremiumsBefore2017(before.kind) && values.kind === before.kind) {
    const premiums = toWireAmount(values.premiumsBefore2017.replace(/\s/g, ''));
    const current = before.premiumsBefore2017?.amount;
    if (premiums === '') {
      if (current != null) body.clearPremiumsBefore2017 = true;
    } else if (premiums !== current) {
      body.premiumsBefore2017 = premiums;
    }
  }

  // A savings envelope's livret; off savings the server drops it on its own.
  if (values.kind === 'savings' && values.regulated !== (before.regulated ?? 'none')) {
    if (values.regulated === 'none') body.clearRegulated = true; else body.regulated = values.regulated;
  }

  return Object.keys(body).length === 0 ? null : body;
}

/** The wire body: the server binds a missing member to null, so every `clear*` flag is stated. */
export function toWireBody(body: EditBody): components['schemas']['EditEnvelopeBody'] {
  return {
    clearInstitution: false, clearAccountHint: false, clearOpenedOn: false, clearStaleAfterDays: false, clearNote: false, clearUcShareFloorPct: false,
    ...body,
  };
}

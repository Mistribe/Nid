import type { components } from '@/api/schema';
import type { IncomeSourceDetail } from '@/api/income';
import { ownershipChange, type OwnershipValues } from '@/components/ownership/ownership';

/** What the settings form holds: every field as typed, compared and sent trimmed. */
export type SettingsValues = { displayName: string; kind: string; payerLabel: string; note: string } & Partial<OwnershipValues>;

/** Only the fields that changed; an emptied optional field says so with its `clear*` flag. */
export type EditBody = Partial<components['schemas']['EditIncomeSourceBody']>;

const OPTIONAL_TEXT = [['payerLabel', 'clearPayerLabel'], ['note', 'clearNote']] as const;

/** `null` when nothing changed, so the form can say so instead of sending an empty patch. */
export function toEditBody(before: IncomeSourceDetail, values: SettingsValues): EditBody | null {
  const body: EditBody = {};
  const name = values.displayName.trim();
  if (name !== before.displayName.trim()) body.displayName = name;
  if (values.kind !== before.kind) body.kind = values.kind;
  for (const [field, clear] of OPTIONAL_TEXT) {
    const next = values[field].trim();
    if (next === (before[field] ?? '').trim()) continue;
    if (next === '') body[clear] = true; else body[field] = next;
  }
  // #69: the sharing and the owners, only where the form shows them (the source's owners).
  Object.assign(body, ownershipChange(before, values));
  return Object.keys(body).length === 0 ? null : body;
}

/** The wire body: the server binds a missing member to null, so every `clear*` flag is stated. */
export function toWireBody(body: EditBody): components['schemas']['EditIncomeSourceBody'] {
  return { clearPayerLabel: false, clearNote: false, ...body };
}

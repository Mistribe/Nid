import type { components } from '@/api/schema';
import type { LoanDetail } from '@/api/loans';

/** What the settings form holds: every field as typed, compared and sent trimmed; `propertyId` is '' for « Aucun ». */
export type SettingsValues = { name: string; lender: string; note: string; propertyId: string };

/** Only the fields that changed; an emptied optional field says so with its `clear*` flag. */
export type EditBody = Partial<components['schemas']['EditLoanBody']>;

const OPTIONAL_TEXT = [['lender', 'clearLender'], ['note', 'clearNote']] as const;

/** `null` when nothing changed, so the form can say so instead of sending an empty patch. */
export function toEditBody(before: LoanDetail, values: SettingsValues): EditBody | null {
  const body: EditBody = {};
  const name = values.name.trim();
  if (name !== before.name.trim()) body.name = name;
  for (const [field, clear] of OPTIONAL_TEXT) {
    const next = values[field].trim();
    if (next === (before[field] ?? '').trim()) continue;
    if (next === '') body[clear] = true; else body[field] = next;
  }
  // 3D1 design §7: « Aucun » unlinks; an unchanged link (even an unreadable one) sends nothing.
  if (values.propertyId !== (before.propertyId ?? '')) {
    if (values.propertyId === '') body.clearProperty = true; else body.propertyId = values.propertyId;
  }
  return Object.keys(body).length === 0 ? null : body;
}

/** The wire body: `clearLender`, `clearNote` and `clearProperty` are required members, so all are stated. */
export function toWireBody(body: EditBody): components['schemas']['EditLoanBody'] {
  return { clearLender: false, clearNote: false, clearProperty: false, ...body };
}

import type { TFunction } from 'i18next';
import { z } from 'zod';
import type { AssetCost } from '@/api/assets';
import { ASSET_COST_CATEGORIES } from '@/api/assets';
import type { components } from '@/api/schema';
import { ISO_DATE, POSITIVE_AMOUNT, toFieldAmount, toWireAmount } from '@/lib/amount';
import { todayIso } from '@/lib/today';
import '@/locales/assets';
import '@/locales/investments';
import '@/locales/loans';
import '@/locales/forms';

/** The server's bounds: dated on or before today, positive, two decimals, a category of the list; the invoice is optional. */
export function costSchema(t: TFunction) {
  return z.object({
    category: z.enum(ASSET_COST_CATEGORIES),
    occurredOn: z.string().regex(ISO_DATE, t('edit.required')).refine((v) => v <= todayIso(), t('field_error.core_date_in_future')),
    amount: z.string().trim().regex(POSITIVE_AMOUNT, t('loan_form.amount_format')),
    note: z.string().max(2000, t('investments_form.note_too_long')),
    documentId: z.string(),
  });
}

export type CostValues = z.infer<ReturnType<typeof costSchema>>;

/** A server path to the field that owns it: the body's names are the form's. */
export const COST_PATHS = { category: 'category', occurredOn: 'occurredOn', amount: 'amount', note: 'note', documentId: 'documentId' } as const satisfies Record<string, keyof CostValues>;

export function costValuesOf(c?: AssetCost): CostValues {
  return c
    ? { category: c.category as CostValues['category'], occurredOn: c.occurredOn, amount: toFieldAmount(c.amount.amount), note: c.note ?? '', documentId: c.documentId ?? '' }
    : { category: 'service', occurredOn: todayIso(), amount: '', note: '', documentId: '' };
}

/** The body, the same for an addition and a correction: « Aucune facture » on a cost that had one clears it, on a new cost it sends nothing. */
export function toCostBody(v: CostValues, editing?: AssetCost): components['schemas']['AssetCostBody'] {
  return {
    occurredOn: v.occurredOn, category: v.category, amount: toWireAmount(v.amount),
    ...(v.note.trim() ? { note: v.note } : {}), ...(v.documentId ? { documentId: v.documentId } : editing?.documentId ? { clearDocument: true } : {}),
  };
}

export const categoryText = (t: TFunction, category: string) =>
  t(`assets_costs.category_option.${(ASSET_COST_CATEGORIES as readonly string[]).includes(category) ? category : 'other'}` as 'assets_costs.category_option.other');

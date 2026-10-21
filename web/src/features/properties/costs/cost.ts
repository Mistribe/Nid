import type { TFunction } from 'i18next';
import { z } from 'zod';
import type { components } from '@/api/schema';
import { LOAN_AMOUNT } from '../../loans/new/loanForm';
import { ISO_DATE, toWireAmount } from '../../subscriptions/edits/amount';
import { todayIso } from '../../subscriptions/today';
import type { PropertyRecurringCost } from '../api';
import { EVERY_UNITS, RECURRING_CATEGORIES } from '../format';
import '@/locales/investments';
import '@/locales/loans';

/** A count of periods, not an amount: a plain whole number from 1 to 120 (3C2 §2). */
const everyCount = (v: string) => /^\d{1,3}$/.test(v) && Number(v) >= 1 && Number(v) <= 120;

/** The server's bounds (spec 3C2 §2): a category but works, a positive amount, every 1 to 120 months or years, an end after the start. */
export function costSchema(t: TFunction) {
  return z.object({
    category: z.enum(RECURRING_CATEGORIES),
    amount: z.string().trim().regex(LOAN_AMOUNT, t('loan_form.amount_format')),
    everyCount: z.string().trim().refine(everyCount, t('property_costs.every_range')),
    everyUnit: z.enum(EVERY_UNITS),
    startsOn: z.string().regex(ISO_DATE, t('edit.required')),
    endsOn: z.string(),
    note: z.string().max(2000, t('investments_form.note_too_long')),
  }).superRefine((v, ctx) => {
    if (v.endsOn && v.endsOn <= v.startsOn) ctx.addIssue({ code: 'custom', path: ['endsOn'], message: t('field_error.subscriptions_phase_range') });
  });
}

export type CostValues = z.infer<ReturnType<typeof costSchema>>;

/** A server path to the field that owns it: the period's two parts land on their own field, a missing period on its count. */
export const COST_PATHS = {
  category: 'category', amount: 'amount', every: 'everyCount', 'every.count': 'everyCount', 'every.unit': 'everyUnit',
  startsOn: 'startsOn', endsOn: 'endsOn', note: 'note',
} as const satisfies Record<string, keyof CostValues>;

/** A new cost: a property tax, once a year, from today; a stored one as the server wrote it, an absent end or note empty. */
export function costValuesOf(c?: PropertyRecurringCost): CostValues {
  // The server writes only its own categories and units.
  return c
    ? {
      category: c.category as CostValues['category'], amount: c.amount, everyCount: String(c.every.count),
      everyUnit: c.every.unit as CostValues['everyUnit'], startsOn: c.startsOn, endsOn: c.endsOn ?? '', note: c.note ?? '',
    }
    : { category: 'property_tax', amount: '', everyCount: '1', everyUnit: 'year', startsOn: todayIso(), endsOn: '', note: '' };
}

/** The whole cost — a `PUT` replaces every value, so an emptied end or note is left out; `version` on a correction only. */
export function toCostBody(v: CostValues, version?: number): components['schemas']['PropertyRecurringCostBody'] {
  return {
    category: v.category, amount: toWireAmount(v.amount), every: { count: Number(v.everyCount), unit: v.everyUnit }, startsOn: v.startsOn,
    ...(v.endsOn ? { endsOn: v.endsOn } : {}),
    ...(v.note.trim() ? { note: v.note } : {}),
    ...(version === undefined ? {} : { version }),
  };
}

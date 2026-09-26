import type { TFunction } from 'i18next';
import { z } from 'zod';
import type { components } from '@/api/schema';
import { AMOUNT, CURRENCY, ISO_DATE, toWireAmount } from '../edits/amount';

const UNITS = ['day', 'week', 'month', 'year'] as const;
export type Unit = (typeof UNITS)[number];

/**
 * A share typed in percent as basis points, by reading the text (`33.33` → 3333): no floating point touches it.
 * At most two decimals and at most 100 %; anything else is null.
 */
export function percentToBp(text: string): number | null {
  const match = /^(\d{1,3})(?:[.,](\d{1,2}))?$/.exec(text.trim());
  if (!match) return null;
  const bp = Number(match[1]) * 100 + Number((match[2] ?? '').padEnd(2, '0'));
  return bp <= 10000 ? bp : null;
}

const count = (t: TFunction) => z.string().regex(/^[1-9]\d*$/, t('edit.invalid_count'));

export function wizardSchema(t: TFunction) {
  return z.object({
    provider: z.object({ key: z.string(), name: z.string() }).nullable(),
    displayName: z.string().trim().min(1, t('edit.required')),
    visibility: z.enum(['shared', 'private']),
    owners: z.array(z.object({ personId: z.string().min(1, t('edit.required')), share: z.string() })).min(1, t('edit.required')),
    users: z.array(z.string()),
    note: z.string(),
    startsOn: z.string().regex(ISO_DATE, t('edit.invalid_date')),
    amount: z.string().trim().regex(AMOUNT, t('edit.invalid_amount')),
    currency: z.string().trim().toUpperCase().regex(CURRENCY, t('edit.invalid_currency')),
    everyCount: count(t),
    everyUnit: z.enum(UNITS),
    sameCovers: z.boolean(),
    coversCount: z.string(),
    coversUnit: z.enum(UNITS),
    maxCycles: z.string().regex(/^([1-9]\d*)?$/, t('edit.invalid_count')),
    payerType: z.enum(['household', 'person']),
    payerPerson: z.string(),
  }).superRefine((v, ctx) => {
    const shares = v.owners.map((o) => percentToBp(o.share));
    if (shares.some((bp) => bp === null) || shares.reduce<number>((sum, bp) => sum + (bp ?? 0), 0) !== 10000)
      ctx.addIssue({ code: 'custom', path: ['owners'], message: t('field_error.core_share_sum') });
    if (v.visibility === 'private' && v.owners.length !== 1)
      ctx.addIssue({ code: 'custom', path: ['owners'], message: t('field_error.core_private_single_owner') });
    if (!v.sameCovers && !/^[1-9]\d*$/.test(v.coversCount))
      ctx.addIssue({ code: 'custom', path: ['coversCount'], message: t('edit.invalid_count') });
    if (v.payerType === 'person' && v.payerPerson === '')
      ctx.addIssue({ code: 'custom', path: ['payerPerson'], message: t('edit.required') });
  });
}

export type WizardValues = z.infer<ReturnType<typeof wizardSchema>>;

type CreateBody = components['schemas']['CreateSubscriptionBody'];

/** The form as the single creation request of 1E2a design §2.2. */
export function toCreateBody(v: WizardValues): CreateBody {
  return {
    displayName: v.displayName.trim(),
    visibility: v.visibility,
    owners: v.owners.map((o) => ({ personId: o.personId, shareBp: percentToBp(o.share) ?? 0 })),
    providerKey: v.provider?.key ?? null,
    autoRenew: true,
    users: v.users,
    note: v.note.trim() === '' ? null : v.note,
    phases: [{
      startsOn: v.startsOn,
      endsOn: null,
      price: { amount: toWireAmount(v.amount), currency: v.currency.trim().toUpperCase() },
      every: { count: Number(v.everyCount), unit: v.everyUnit },
      covers: v.sameCovers ? null : { count: Number(v.coversCount), unit: v.coversUnit },
      maxCycles: v.maxCycles === '' ? null : Number(v.maxCycles),
      payer: v.payerType === 'person' ? { type: 'person', personId: v.payerPerson } : { type: 'household', personId: null },
    }],
  };
}

/** Which step owns which server path: a refusal brings the user back to the step where it can be fixed. */
export const STEP_PATHS: Record<number, string[]> = {
  0: ['displayName', 'visibility', 'owners', 'providerKey', 'users', 'note'],
  1: ['phases'],
};

/** A server path as the form field it concerns; the rest shows above the form. */
export function fieldOf(path: string): keyof WizardValues | `owners.${number}.share` | undefined {
  const share = /^owners\[(\d+)\]\.shareBp$/.exec(path);
  if (share) return `owners.${Number(share[1])}.share`;
  const fields: Record<string, keyof WizardValues> = {
    displayName: 'displayName', visibility: 'visibility', owners: 'owners', providerKey: 'provider', users: 'users', note: 'note',
    'phases[0].startsOn': 'startsOn', 'phases[0].endsOn': 'startsOn', 'phases[0].price': 'amount', 'phases[0].price.amount': 'amount',
    'phases[0].price.currency': 'currency', 'phases[0].every': 'everyCount', 'phases[0].every.count': 'everyCount', 'phases[0].every.unit': 'everyUnit',
    'phases[0].covers': 'coversCount', 'phases[0].covers.count': 'coversCount', 'phases[0].covers.unit': 'coversUnit', 'phases[0].maxCycles': 'maxCycles',
    'phases[0].payer': 'payerType', 'phases[0].payer.type': 'payerType', 'phases[0].payer.personId': 'payerPerson',
  };
  return fields[path];
}

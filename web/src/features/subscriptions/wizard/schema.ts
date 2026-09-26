import type { TFunction } from 'i18next';
import type { Path } from 'react-hook-form';
import { z } from 'zod';
import type { components } from '@/api/schema';
import { AMOUNT, CURRENCY, ISO_DATE } from '../edits/amount';
import { offerBody, offerFieldOf, thenMode } from './offers';

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

const POSITIVE_COUNT = /^[1-9]\d*$/;

export function wizardSchema(t: TFunction) {
  return z.object({
    provider: z.object({ key: z.string(), name: z.string() }).nullable(),
    displayName: z.string().trim().min(1, t('edit.required')),
    visibility: z.enum(['shared', 'private']),
    owners: z.array(z.object({ personId: z.string().min(1, t('edit.required')), share: z.string() })).min(1, t('edit.required')),
    users: z.array(z.string()),
    note: z.string(),
    startsOn: z.string(),
    amount: z.string(),
    currency: z.string().trim().toUpperCase().regex(CURRENCY, t('edit.invalid_currency')),
    everyCount: z.string(),
    everyUnit: z.enum(UNITS),
    sameCovers: z.boolean(),
    coversCount: z.string(),
    coversUnit: z.enum(UNITS),
    maxCycles: z.string(),
    payerType: z.enum(['household', 'person']),
    payerPerson: z.string(),
    offer: z.enum(['recurring', 'prepaid', 'trial', 'gift', 'bundle']),
    trialVariant: z.enum(['free', 'launch']),
    offerDate: z.string(),
    offerLabel: z.string(),
    offerAmount: z.string(),
    offerPayerType: z.enum(['household', 'person', 'external']),
    offerPayerPerson: z.string(),
    paidCount: z.string(),
    paidUnit: z.enum(UNITS),
    bonusCount: z.string(),
    bonusUnit: z.enum(UNITS),
    durationCount: z.string(),
    durationUnit: z.enum(UNITS),
    giftReceived: z.boolean(),
    allocated: z.string(),
    launchAmount: z.string(),
    launchCycles: z.string(),
    nextStartsOn: z.string(),
    others: z.array(z.object({ subscriptionId: z.string(), count: z.string(), unit: z.enum(UNITS) })),
    then: z.boolean(),
  }).superRefine((v, ctx) => {
    const shares = v.owners.map((o) => percentToBp(o.share));
    if (shares.some((bp) => bp === null) || shares.reduce<number>((sum, bp) => sum + (bp ?? 0), 0) !== 10000)
      ctx.addIssue({ code: 'custom', path: ['owners'], message: t('field_error.core_share_sum') });
    if (v.visibility === 'private' && v.owners.length !== 1)
      ctx.addIssue({ code: 'custom', path: ['owners'], message: t('field_error.core_private_single_owner') });
    // The 1E2a phase fields: checked only when a phase is actually sent (the recurring offer always sends
    // one, others only when "then" asks for the following phase, or the launch price requires it), so a
    // hidden field left over from an earlier choice never blocks the wizard (final review, finding 1). The
    // launch price never reads `startsOn` (its phases start on `offerDate` and `nextStartsOn`), so it is
    // exempted from that one check, but still reuses `amount`/`everyCount`/`maxCycles` for its normal-price
    // phase.
    const launchPrice = v.offer === 'trial' && v.trialVariant === 'launch';
    if (v.offer === 'recurring' || v.then || thenMode(v) === 'required') {
      if (!launchPrice && !ISO_DATE.test(v.startsOn)) ctx.addIssue({ code: 'custom', path: ['startsOn'], message: t('edit.invalid_date') });
      if (!AMOUNT.test(v.amount.trim())) ctx.addIssue({ code: 'custom', path: ['amount'], message: t('edit.invalid_amount') });
      if (!POSITIVE_COUNT.test(v.everyCount)) ctx.addIssue({ code: 'custom', path: ['everyCount'], message: t('edit.invalid_count') });
      if (!/^([1-9]\d*)?$/.test(v.maxCycles)) ctx.addIssue({ code: 'custom', path: ['maxCycles'], message: t('edit.invalid_count') });
      if (!v.sameCovers && !POSITIVE_COUNT.test(v.coversCount))
        ctx.addIssue({ code: 'custom', path: ['coversCount'], message: t('edit.invalid_count') });
      if (v.payerType === 'person' && v.payerPerson === '')
        ctx.addIssue({ code: 'custom', path: ['payerPerson'], message: t('edit.required') });
    }

    switch (v.offer) {
      case 'prepaid':
        if (!ISO_DATE.test(v.offerDate)) ctx.addIssue({ code: 'custom', path: ['offerDate'], message: t('edit.invalid_date') });
        if (v.offerLabel.trim() === '') ctx.addIssue({ code: 'custom', path: ['offerLabel'], message: t('edit.required') });
        if (!AMOUNT.test(v.offerAmount.trim())) ctx.addIssue({ code: 'custom', path: ['offerAmount'], message: t('edit.invalid_amount') });
        if (!POSITIVE_COUNT.test(v.paidCount)) ctx.addIssue({ code: 'custom', path: ['paidCount'], message: t('edit.invalid_count') });
        if (v.bonusCount.trim() !== '' && !POSITIVE_COUNT.test(v.bonusCount))
          ctx.addIssue({ code: 'custom', path: ['bonusCount'], message: t('edit.invalid_count') });
        break;
      case 'trial':
        if (!ISO_DATE.test(v.offerDate)) ctx.addIssue({ code: 'custom', path: ['offerDate'], message: t('edit.invalid_date') });
        if (v.trialVariant === 'free') {
          if (!POSITIVE_COUNT.test(v.durationCount)) ctx.addIssue({ code: 'custom', path: ['durationCount'], message: t('edit.invalid_count') });
        } else {
          if (!AMOUNT.test(v.launchAmount.trim())) ctx.addIssue({ code: 'custom', path: ['launchAmount'], message: t('edit.invalid_amount') });
          if (!POSITIVE_COUNT.test(v.launchCycles)) ctx.addIssue({ code: 'custom', path: ['launchCycles'], message: t('edit.invalid_count') });
          if (!ISO_DATE.test(v.nextStartsOn) || v.nextStartsOn <= v.offerDate)
            ctx.addIssue({ code: 'custom', path: ['nextStartsOn'], message: t('edit.invalid_date') });
        }
        break;
      case 'gift':
        if (!ISO_DATE.test(v.offerDate)) ctx.addIssue({ code: 'custom', path: ['offerDate'], message: t('edit.invalid_date') });
        if (v.offerLabel.trim() === '') ctx.addIssue({ code: 'custom', path: ['offerLabel'], message: t('edit.required') });
        if (!POSITIVE_COUNT.test(v.durationCount)) ctx.addIssue({ code: 'custom', path: ['durationCount'], message: t('edit.invalid_count') });
        if (!v.giftReceived && !AMOUNT.test(v.offerAmount.trim()))
          ctx.addIssue({ code: 'custom', path: ['offerAmount'], message: t('edit.invalid_amount') });
        break;
      case 'bundle':
        if (!ISO_DATE.test(v.offerDate)) ctx.addIssue({ code: 'custom', path: ['offerDate'], message: t('edit.invalid_date') });
        if (v.offerLabel.trim() === '') ctx.addIssue({ code: 'custom', path: ['offerLabel'], message: t('edit.required') });
        if (!AMOUNT.test(v.offerAmount.trim())) ctx.addIssue({ code: 'custom', path: ['offerAmount'], message: t('edit.invalid_amount') });
        if (!AMOUNT.test(v.allocated.trim())) ctx.addIssue({ code: 'custom', path: ['allocated'], message: t('edit.invalid_amount') });
        if (!POSITIVE_COUNT.test(v.durationCount)) ctx.addIssue({ code: 'custom', path: ['durationCount'], message: t('edit.invalid_count') });
        v.others.forEach((o, i) => {
          if (o.subscriptionId.trim() === '') ctx.addIssue({ code: 'custom', path: ['others', i, 'subscriptionId'], message: t('edit.required') });
          if (!POSITIVE_COUNT.test(o.count)) ctx.addIssue({ code: 'custom', path: ['others', i, 'count'], message: t('edit.invalid_count') });
        });
        break;
      case 'recurring':
        break;
    }
    // Only a purchase that actually records this payer sends it (final review, finding 1): the trial's
    // purchase always pays the household, and a received gift always pays outside it.
    const offerPayerSent = v.offer === 'prepaid' || v.offer === 'bundle' || (v.offer === 'gift' && !v.giftReceived);
    if (offerPayerSent && v.offerPayerType === 'person' && v.offerPayerPerson === '')
      ctx.addIssue({ code: 'custom', path: ['offerPayerPerson'], message: t('edit.required') });
  });
}

export type WizardValues = z.infer<ReturnType<typeof wizardSchema>>;

type CreateBody = components['schemas']['CreateSubscriptionBody'];

/** The form as the single creation request of 1E2b1 design §4.2: the common fields, then the chosen offer's phases and purchases. */
export function toCreateBody(v: WizardValues): CreateBody {
  return {
    displayName: v.displayName.trim(),
    visibility: v.visibility,
    owners: v.owners.map((o) => ({ personId: o.personId, shareBp: percentToBp(o.share) ?? 0 })),
    providerKey: v.provider?.key ?? null,
    autoRenew: true,
    users: v.users,
    note: v.note.trim() === '' ? null : v.note,
    ...offerBody(v),
  };
}

/** A server path as the form field it concerns, common fields first, then by offer (Review Focus 4). */
export const fieldOf = (path: string, v: Pick<WizardValues, 'offer' | 'trialVariant'>): Path<WizardValues> | undefined => {
  const share = /^owners\[(\d+)\]\.shareBp$/.exec(path);
  if (share) return `owners.${Number(share[1])}.share` as const;
  const common = { displayName: 'displayName', visibility: 'visibility', owners: 'owners', providerKey: 'provider', users: 'users', note: 'note' } as const;
  return common[path as keyof typeof common] ?? offerFieldOf(path, v);
};

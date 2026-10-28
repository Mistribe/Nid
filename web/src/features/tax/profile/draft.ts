import type { components } from '@/api/schema';
import { AMOUNT, toWireAmount } from '../../subscriptions/edits/amount';
import type { PinelCommitment, TaxEstimate } from '../api';
import { ZERO } from '../format';

/** A Pinel commitment as one choice: the initial years, then the total after the extensions. */
export const ENGAGEMENTS = ['6-6', '9-9', '6-9', '6-12', '9-12'] as const;
const COUNT = /^\d{1,2}$/;
/** A percent as typed (`23,5`): empty keeps the status's rate. */
export const RATE = /^\d{1,2}([.,]\d{1,2})?$/;

const AMOUNT_KEYS = ['childcareOutsideHome', 'homeEmployment', 'donations', 'donationsAid', 'rentalDeficitCarried'] as const;
/** The per-adult maps, keyed by adult id: an empty text keeps the default. */
const PER_ADULT = { rates: RATE, expenses: AMOUNT, unused: AMOUNT, ceilings: AMOUNT, expiring: AMOUNT, exempt: AMOUNT } as const;
type PerAdult = keyof typeof PER_ADULT;

/** The profile as typed: every amount, rate and count is the text in its field. */
export type ProfileDraft = {
  statuses: Record<string, string>; dependents: string; under6: string; regime: string;
  amounts: Record<(typeof AMOUNT_KEYS)[number], string>; pinel: PinelCommitment[]; pooling: boolean;
} & Record<PerAdult, Record<string, string>>;

export type DraftField = 'dependents' | 'under6' | `amounts.${string}` | `${PerAdult}.${string}`;
export type DraftErrors = Partial<Record<DraftField, 'amount' | 'rate' | 'count'>>;
export type ProfileBody = components['schemas']['TaxProfileBody'];

/**
 * The notice's 4BD only suggests: it pre-fills an empty field, editable, and is saved when the user saves. Only where the
 * field shows (a rental property): a hidden suggestion would be saved unseen.
 */
export function suggestedDeficit(estimate: TaxEstimate): string | undefined {
  const carried = estimate.carriedFrom?.rentalDeficitCarried;
  return estimate.rentalProperties.length && carried !== null && carried !== undefined && ZERO.test(estimate.profile.rentalDeficitCarried.trim())
    ? carried : undefined;
}

/** Tax estimate spec §7: the household's declarations for the year, as the profile tab starts from. */
export function initialDraft(estimate: TaxEstimate): ProfileDraft {
  const p = estimate.profile;
  return {
    statuses: Object.fromEntries(estimate.adults.map((a) => [a.id, p.statuses[a.id] ?? 'non_executive'])),
    dependents: p.dependents === null || p.dependents === undefined ? '' : String(p.dependents),
    under6: String(p.childrenUnder6), regime: p.rentalRegime ?? 'auto',
    amounts: { ...Object.fromEntries(AMOUNT_KEYS.map((k) => [k, p[k]])), rentalDeficitCarried: suggestedDeficit(estimate) ?? p.rentalDeficitCarried } as ProfileDraft['amounts'],
    pinel: p.pinel, pooling: p.perPooling,
    rates: p.contributionRates, expenses: p.actualExpenses, unused: p.perUnusedCeilings, ceilings: p.perCeilings,
    expiring: p.perCeilingsExpiring, exempt: p.exemptIncomes,
  };
}

/** Only the adults' typed values go back: an empty one keeps the default. */
const filled = (values: Record<string, string>) =>
  Object.fromEntries(Object.entries(values).filter(([, v]) => v.trim() !== '').map(([k, v]) => [k, toWireAmount(v)]));

/** The body of `PUT /tax/profile/{year}`: text only, never a number computed here. */
export function profileBody(d: ProfileDraft): ProfileBody {
  return {
    statuses: d.statuses, dependents: d.dependents === '' ? null : Number(d.dependents), childrenUnder6: Number(d.under6),
    rentalRegime: d.regime === 'auto' ? null : d.regime, pinel: d.pinel, contributionRates: filled(d.rates),
    actualExpenses: filled(d.expenses), perUnusedCeilings: filled(d.unused), perCeilings: filled(d.ceilings),
    perCeilingsExpiring: filled(d.expiring), perPooling: d.pooling, exemptIncomes: filled(d.exempt),
    rentalDeficitCarried: toWireAmount(d.amounts.rentalDeficitCarried),
    childcareOutsideHome: toWireAmount(d.amounts.childcareOutsideHome), homeEmployment: toWireAmount(d.amounts.homeEmployment),
    donations: toWireAmount(d.amounts.donations), donationsAid: toWireAmount(d.amounts.donationsAid),
  };
}

/** Each field whose text the API would refuse, with the format it should follow. */
export function draftErrors(d: ProfileDraft): DraftErrors {
  const errors: DraftErrors = {};
  if (d.dependents !== '' && !COUNT.test(d.dependents)) errors.dependents = 'count';
  if (!COUNT.test(d.under6)) errors.under6 = 'count';
  for (const k of AMOUNT_KEYS) if (!AMOUNT.test(d.amounts[k].trim())) errors[`amounts.${k}`] = 'amount';
  for (const map of Object.keys(PER_ADULT) as PerAdult[]) {
    for (const [id, v] of Object.entries(d[map])) {
      if (v.trim() !== '' && !PER_ADULT[map].test(v.trim())) errors[`${map}.${id}`] = map === 'rates' ? 'rate' : 'amount';
    }
  }
  return errors;
}

/** How many fields differ from the saved profile, as typed (trimmed); a Pinel row counts once. */
export function countChanges(d: ProfileDraft, initial: ProfileDraft): number {
  const text = (v: string | undefined) => (v ?? '').trim();
  const diff = (a: Record<string, string>, b: Record<string, string>) =>
    [...new Set([...Object.keys(a), ...Object.keys(b)])].filter((k) => text(a[k]) !== text(b[k])).length;
  const pinelKey = (c: PinelCommitment | undefined) => (c ? `${c.initialYears}-${c.totalYears}-${c.plus}` : '');
  const properties = new Set([...d.pinel, ...initial.pinel].map((c) => c.property));
  return diff(d.statuses, initial.statuses)
    + Number(text(d.dependents) !== text(initial.dependents)) + Number(text(d.under6) !== text(initial.under6))
    + Number(d.regime !== initial.regime) + Number(d.pooling !== initial.pooling)
    + diff(d.amounts, initial.amounts)
    + (Object.keys(PER_ADULT) as PerAdult[]).reduce((n, map) => n + diff(d[map], initial[map]), 0)
    + [...properties].filter((p) => pinelKey(d.pinel.find((c) => c.property === p)) !== pinelKey(initial.pinel.find((c) => c.property === p))).length;
}

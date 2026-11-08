import type { components } from '@/api/schema';
import { AMOUNT, toFieldAmount, toWireAmount } from '@/lib/amount';
import type { PinelCommitment, TaxEstimate } from '@/api/tax';
import { ZERO } from '../format';

/** A Pinel commitment as one choice: the initial years, then the total after the extensions. */
export const ENGAGEMENTS = ['6-6', '9-9', '6-9', '6-12', '9-12'] as const;
const COUNT = /^\d{1,2}$/;
/** A department code, as the server checks it (`TaxCalendar.IsDepartment`): 01 to 95 but 20, 2A, 2B, 971 to 976. */
const DEPARTMENT = /^(0[1-9]|1\d|2[1-9AB]|[3-8]\d|9[0-5]|97[1-6])$/i;
/** A percent as typed (`23,5`): empty keeps the status's rate. */
export const RATE = /^\d{1,2}([.,]\d{1,2})?$/;

const AMOUNT_KEYS = ['childcareOutsideHome', 'homeEmployment', 'donations', 'donationsAid', 'donationsAidLate', 'rentalDeficitCarried', 'rentalEnergyWorks'] as const;
/** The per-adult maps, keyed by adult id: an empty text keeps the default. */
const PER_ADULT = { rates: RATE, expenses: AMOUNT, unused: AMOUNT, ceilings: AMOUNT, expiring: AMOUNT, exempt: AMOUNT } as const;
type PerAdult = keyof typeof PER_ADULT;

/** The profile as typed: every amount, rate and count is the text in its field. */
export type ProfileDraft = {
  statuses: Record<string, string>; dependents: string; under6: string; regime: string; department: string;
  amounts: Record<(typeof AMOUNT_KEYS)[number], string>; pinel: PinelCommitment[]; pooling: boolean;
} & Record<PerAdult, Record<string, string>>;

export type DraftField = 'dependents' | 'under6' | 'department' | `amounts.${string}` | `${PerAdult}.${string}`;
export type DraftErrors = Partial<Record<DraftField, 'amount' | 'rate' | 'count' | 'department'>>;
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

/** The server's figures as a French reader types them (`800.00` → `800,00`). */
const fields = (values: Record<string, string>) => Object.fromEntries(Object.entries(values).map(([k, v]) => [k, toFieldAmount(v)]));

/** Tax estimate spec §7: the household's declarations for the year, as the profile tab starts from. */
export function initialDraft(estimate: TaxEstimate): ProfileDraft {
  const p = estimate.profile;
  return {
    statuses: Object.fromEntries(estimate.adults.map((a) => [a.id, p.statuses[a.id] ?? 'non_executive'])),
    dependents: p.dependents === null || p.dependents === undefined ? '' : String(p.dependents),
    under6: String(p.childrenUnder6), regime: p.rentalRegime ?? 'auto', department: p.department ?? '',
    amounts: fields({ ...Object.fromEntries(AMOUNT_KEYS.map((k) => [k, p[k]])), rentalDeficitCarried: suggestedDeficit(estimate) ?? p.rentalDeficitCarried }) as ProfileDraft['amounts'],
    pinel: p.pinel, pooling: p.perPooling,
    rates: fields(p.contributionRates), expenses: fields(p.actualExpenses), unused: fields(p.perUnusedCeilings), ceilings: fields(p.perCeilings),
    expiring: fields(p.perCeilingsExpiring), exempt: fields(p.exemptIncomes),
  };
}

/** Only the adults' typed values go back: an empty one keeps the default. */
const filled = (values: Record<string, string>) =>
  Object.fromEntries(Object.entries(values).filter(([, v]) => v.trim() !== '').map(([k, v]) => [k, toWireAmount(v)]));

/** The body of `PUT /tax/profile/{year}`: text only, never a number computed here. */
export function profileBody(d: ProfileDraft): ProfileBody {
  return {
    statuses: d.statuses, dependents: d.dependents === '' ? null : Number(d.dependents), childrenUnder6: Number(d.under6),
    rentalRegime: d.regime === 'auto' ? null : d.regime, department: d.department.trim() || null, pinel: d.pinel, contributionRates: filled(d.rates),
    actualExpenses: filled(d.expenses), perUnusedCeilings: filled(d.unused), perCeilings: filled(d.ceilings),
    perCeilingsExpiring: filled(d.expiring), perPooling: d.pooling, exemptIncomes: filled(d.exempt),
    rentalDeficitCarried: toWireAmount(d.amounts.rentalDeficitCarried),
    childcareOutsideHome: toWireAmount(d.amounts.childcareOutsideHome), homeEmployment: toWireAmount(d.amounts.homeEmployment),
    donations: toWireAmount(d.amounts.donations), donationsAid: toWireAmount(d.amounts.donationsAid),
    donationsAidLate: toWireAmount(d.amounts.donationsAidLate), rentalEnergyWorks: toWireAmount(d.amounts.rentalEnergyWorks),
  };
}

/** Each field whose text the API would refuse, with the format it should follow. */
export function draftErrors(d: ProfileDraft): DraftErrors {
  const errors: DraftErrors = {};
  if (d.dependents !== '' && !COUNT.test(d.dependents)) errors.dependents = 'count';
  if (!COUNT.test(d.under6)) errors.under6 = 'count';
  if (d.department.trim() !== '' && !DEPARTMENT.test(d.department.trim())) errors.department = 'department';
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
    + Number(d.regime !== initial.regime) + Number(d.pooling !== initial.pooling) + Number(text(d.department) !== text(initial.department))
    + diff(d.amounts, initial.amounts)
    + (Object.keys(PER_ADULT) as PerAdult[]).reduce((n, map) => n + diff(d[map], initial[map]), 0)
    + [...properties].filter((p) => pinelKey(d.pinel.find((c) => c.property === p)) !== pinelKey(initial.pinel.find((c) => c.property === p))).length;
}

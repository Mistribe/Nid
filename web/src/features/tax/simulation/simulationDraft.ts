import type { TaxEstimate, TaxInputs } from '@/api/tax';
import { AMOUNT, toFieldAmount, toWireAmount, trimAmount } from '@/lib/amount';
import { ZERO } from '../format';
import { RATE } from '../profile/draft';

export const GROUPS = ['activity', 'per', 'household', 'rental', 'capital', 'expenses'] as const;
export type GroupId = (typeof GROUPS)[number];

export const HOUSEHOLD_AMOUNTS = ['rentalGross', 'rentalInterest', 'rentalCharges', 'capitalIncome', 'capitalDividends', 'deductibleCsg', 'capitalLossesCarried', 'nonTaxableIncome', 'pinelReduction',
  'childcareOutsideHome', 'homeEmployment', 'donations', 'donationsAid', 'rentalDeficitCarried'] as const;
export const DECLARANT_AMOUNTS = ['salaryGross', 'salaryTaxable', 'otherTaxable', 'perContributions', 'perUnusedCeiling', 'previousYearIncome', 'perExpiring', 'exemptIncome',
  'contributionRate', 'actualExpenses', 'perCeiling'] as const;
/** The declarant fields the household may leave empty (the server then keeps its default). */
const OPTIONAL = ['contributionRate', 'actualExpenses', 'perCeiling'] as const;
type HouseholdAmount = (typeof HOUSEHOLD_AMOUNTS)[number];
type DeclarantAmount = (typeof DECLARANT_AMOUNTS)[number];
export type FieldKey = HouseholdAmount | DeclarantAmount | 'dependents' | 'childrenUnder6';

/** Where each field sits in the form; a `rare` one is folded under « Personnaliser ». */
export const FIELDS: Record<FieldKey, { group: GroupId; rare?: true }> = {
  salaryGross: { group: 'activity' }, otherTaxable: { group: 'activity' },
  salaryTaxable: { group: 'activity', rare: true }, contributionRate: { group: 'activity', rare: true },
  actualExpenses: { group: 'activity', rare: true }, exemptIncome: { group: 'activity', rare: true },
  perContributions: { group: 'per' },
  perCeiling: { group: 'per', rare: true }, perUnusedCeiling: { group: 'per', rare: true }, perExpiring: { group: 'per', rare: true }, previousYearIncome: { group: 'per', rare: true },
  dependents: { group: 'household' }, childrenUnder6: { group: 'household' }, nonTaxableIncome: { group: 'household', rare: true },
  rentalGross: { group: 'rental' }, rentalCharges: { group: 'rental' }, rentalInterest: { group: 'rental' },
  rentalDeficitCarried: { group: 'rental', rare: true }, pinelReduction: { group: 'rental', rare: true },
  capitalIncome: { group: 'capital' }, capitalDividends: { group: 'capital' },
  deductibleCsg: { group: 'capital', rare: true }, capitalLossesCarried: { group: 'capital', rare: true },
  childcareOutsideHome: { group: 'expenses' }, homeEmployment: { group: 'expenses' }, donations: { group: 'expenses' }, donationsAid: { group: 'expenses' },
};

/** The inputs as typed: amounts in field format (`64704,14`), counts as text. */
export type SimulationDraft = { inputs: TaxInputs; counts: { dependents: string; childrenUnder6: string } };
/** Keyed `salaryGross.0` for a declarant's field, `rentalGross` / `dependents` for the household's. */
export type DraftErrors = Partial<Record<string, 'amount' | 'rate' | 'count'>>;

const COUNT = /^\d{1,2}$/;
const field = (v: string) => toFieldAmount(trimAmount(v));
const isOptional = (k: DeclarantAmount): k is (typeof OPTIONAL)[number] => (OPTIONAL as readonly string[]).includes(k);

export function toDraft(inputs: TaxInputs): SimulationDraft {
  const house = Object.fromEntries(HOUSEHOLD_AMOUNTS.map((k) => [k, field(inputs[k])]));
  return {
    inputs: {
      ...inputs, ...house,
      declarants: inputs.declarants.map((d) => ({
        ...d, ...Object.fromEntries(DECLARANT_AMOUNTS.map((k) => [k, d[k] == null ? '' : field(d[k])])),
      })),
    },
    counts: { dependents: String(inputs.dependents), childrenUnder6: String(inputs.childrenUnder6) },
  };
}

export function draftErrors(d: SimulationDraft): DraftErrors {
  const errors: DraftErrors = {};
  const wire = (v: string | null | undefined) => toWireAmount(v ?? '');
  for (const k of HOUSEHOLD_AMOUNTS) if (!AMOUNT.test(wire(d.inputs[k]))) errors[k] = 'amount';
  d.inputs.declarants.forEach((dec, i) => {
    for (const k of DECLARANT_AMOUNTS) {
      const text = wire(dec[k]);
      if (isOptional(k) && text === '') continue;
      if (k === 'contributionRate' ? !RATE.test(text) : !AMOUNT.test(text)) errors[`${k}.${i}`] = k === 'contributionRate' ? 'rate' : 'amount';
    }
  });
  for (const k of ['dependents', 'childrenUnder6'] as const) if (!COUNT.test(d.counts[k])) errors[k] = 'count';
  return errors;
}

export const groupsWithErrors = (e: DraftErrors): Set<GroupId> =>
  new Set(Object.keys(e).map((key) => FIELDS[key.split('.')[0] as FieldKey].group));

/** The body of `POST /tax/simulate`: text only, never a number computed here. */
export function toBody(d: SimulationDraft): TaxInputs {
  return {
    ...d.inputs,
    ...Object.fromEntries(HOUSEHOLD_AMOUNTS.map((k) => [k, toWireAmount(d.inputs[k])])),
    dependents: Number(d.counts.dependents), childrenUnder6: Number(d.counts.childrenUnder6),
    declarants: d.inputs.declarants.map((dec) => ({
      ...dec,
      ...Object.fromEntries(DECLARANT_AMOUNTS.map((k) => [k, isOptional(k) && !dec[k]?.trim() ? null : toWireAmount(dec[k] ?? '')])),
    })),
  };
}

export const SCENARIOS = ['per', 'salaries', 'real', 'child'] as const;
export type ScenarioId = (typeof SCENARIOS)[number];

const perRoom = (estimate: TaxEstimate) => estimate.result.declarants.map((d) => (d.per && !ZERO.test(d.per.remaining) ? d.per : null));

export function scenarios(estimate: TaxEstimate): ScenarioId[] {
  const real = estimate.result.rentalRegime === 'micro' && !ZERO.test(estimate.inputs.rentalGross);
  return SCENARIOS.filter((id) => (id === 'per' ? perRoom(estimate).some(Boolean) : id === 'real' ? real : true));
}

/** Each scenario starts from the estimate's own inputs and changes one thing, opening the group where it shows. */
export function applyScenario(id: ScenarioId, estimate: TaxEstimate): { draft: SimulationDraft; open: GroupId[] } {
  const draft = toDraft(estimate.inputs);
  switch (id) {
    case 'per': {
      const room = perRoom(estimate);
      draft.inputs.declarants = draft.inputs.declarants.map((d, i) => {
        const per = room[i];
        return per ? { ...d, perContributions: toFieldAmount(trimAmount(per.ceiling)) } : d;
      });
      return { draft, open: ['per'] };
    }
    case 'salaries': return { draft, open: ['activity'] };
    case 'real': return { draft: { ...draft, inputs: { ...draft.inputs, rentalRegime: 'real' } }, open: ['rental'] };
    case 'child': {
      const more = (n: string) => String(Number(n) + 1);
      return { draft: { ...draft, counts: { dependents: more(draft.counts.dependents), childrenUnder6: more(draft.counts.childrenUnder6) } }, open: ['household'] };
    }
  }
}

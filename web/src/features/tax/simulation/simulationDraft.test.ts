import { describe, expect, it } from 'vitest';
import type { TaxInputs, TaxResult } from '@/api/tax';
import { estimate, result } from '../taxTestData';
import { applyScenario, draftErrors, fieldOf, groupsWithErrors, scenarios, toBody, toDraft } from './simulationDraft';

type Declarant = TaxInputs['declarants'][number];
const first = (inputs: TaxInputs) => inputs.declarants[0] as Declarant;

const withDeclarant = (over: Record<string, string | null>) => {
  const d = toDraft(estimate().inputs);
  d.inputs.declarants = [{ ...first(d.inputs), ...over }, { ...first(d.inputs), name: 'Bob' }];
  return d;
};

describe('simulationDraft', () => {
  it('toDraft shows the amounts as typed in French', () => {
    const e = estimate();
    first(e.inputs).salaryGross = '64704.14';
    first(e.inputs).contributionRate = null;
    const d = toDraft(e.inputs);
    expect(first(d.inputs).salaryGross).toBe('64704,14');
    expect(d.inputs.childcareOutsideHome).toBe('6000');
    expect(first(d.inputs).contributionRate).toBe('');
    expect(d.counts).toEqual({ dependents: '2', childrenUnder6: '2' });
  });

  it('toBody(toDraft(inputs)) gives back the inputs, number for number', () => {
    const e = estimate();
    first(e.inputs).contributionRate = '7.5';
    first(e.inputs).actualExpenses = null;
    first(e.inputs).perCeiling = null;
    first(e.inputs).salaryGross = '64704.14';
    e.inputs.rentalGross = '7200.50';
    const norm = (v: unknown): unknown => (typeof v === 'string' && /^-?\d+(\.\d+)?$/.test(v) ? Number(v)
      : Array.isArray(v) ? v.map(norm) : v && typeof v === 'object' ? Object.fromEntries(Object.entries(v).map(([k, x]) => [k, norm(x)])) : v);
    const body = toBody(toDraft(e.inputs));
    expect(norm(body)).toEqual(norm(e.inputs));
    expect(first(body).actualExpenses).toBeNull();
    expect(body.dependents).toBe(2);
    expect(body.childrenUnder6).toBe(2);
  });

  it('toBody sends the wire format', () => {
    const d = withDeclarant({ salaryGross: '60 000,50', actualExpenses: '' });
    d.counts.dependents = '3';
    const body = toBody(d);
    expect(body.declarants[0]?.salaryGross).toBe('60000.50');
    expect(body.declarants[0]?.actualExpenses).toBeNull();
    expect(body.dependents).toBe(3);
    expect(body.declarants[0]?.pensions).toBe('0.00');
    expect(body.singleParent).toBe(false);
  });

  it('draftErrors names the field and its declarant', () => {
    expect(draftErrors(withDeclarant({ salaryGross: 'abc' }))).toEqual({ 'salaryGross.0': 'amount' });
    expect(draftErrors(withDeclarant({ contributionRate: '120' }))).toEqual({ 'contributionRate.0': 'rate' });
    const d = toDraft(estimate().inputs);
    d.counts.dependents = 'x';
    expect(draftErrors(d)).toEqual({ dependents: 'count' });
    d.counts.dependents = '1';
    d.inputs.donations = '1 000,5';
    expect(draftErrors(d)).toEqual({});
  });

  it('fieldOf names the field of an error key and ignores unknown ones', () => {
    expect(fieldOf('salaryGross.1')).toBe('salaryGross');
    expect(fieldOf('dependents')).toBe('dependents');
    expect(fieldOf('toString')).toBeUndefined();
    expect(groupsWithErrors({ nope: 'amount' })).toEqual(new Set());
  });

  it('groupsWithErrors maps each error to its group', () => {
    expect(groupsWithErrors({ donations: 'amount', 'perCeiling.0': 'amount' })).toEqual(new Set(['expenses', 'per']));
  });

  it('the child scenario adds a dependent under 6', () => {
    const { draft, open } = applyScenario('child', estimate());
    expect(draft.counts).toEqual({ dependents: '3', childrenUnder6: '3' });
    expect(open).toEqual(['household']);
  });

  it('the PER scenario fills each declarant up to the server’s ceiling', () => {
    const per = { ceiling: '4500.00', source: 'x', expiring: '0.00', contributions: '0.00', deducted: '0.00', excess: '0.00', remaining: '4500.00', minimum: '0.00', minimumEstimated: false };
    const e = estimate();
    e.result = result({ declarants: [{ ...(e.result.declarants[0] as TaxResult['declarants'][number]), per }] });
    expect(first(applyScenario('per', e).draft.inputs).perContributions).toBe('4500');
    (e.result.declarants[0] as TaxResult['declarants'][number]).per = { ...per, remaining: '0.00' };
    expect(first(applyScenario('per', e).draft.inputs).perContributions).toBe('0');
  });

  it('the PER scenario leaves a declarant who is in excess unchanged', () => {
    const per = { ceiling: '4500.00', source: 'x', expiring: '0.00', contributions: '0.00', deducted: '0.00', excess: '0.00', remaining: '3000.00', minimum: '0.00', minimumEstimated: false };
    const e = estimate();
    const alice = e.result.declarants[0] as TaxResult['declarants'][number];
    e.inputs.declarants.push({ ...first(e.inputs), name: 'Bob' });
    first(e.inputs).perContributions = '5200.00';
    e.result = result({ declarants: [{ ...alice, per: { ...per, contributions: '5200.00', excess: '700.00' } }, { ...alice, name: 'Bob', per }] });
    const { inputs } = applyScenario('per', e).draft;
    expect(inputs.declarants.map((d) => d.perContributions)).toEqual(['5200', '4500']);
  });

  it('the real scenario is offered only for rents under the micro-foncier', () => {
    const e = estimate();
    expect(scenarios(e)).not.toContain('real');
    e.inputs.rentalGross = '7200.00';
    e.result = result({ rentalRegime: 'micro' });
    expect(scenarios(e)).toContain('real');
    expect(applyScenario('real', e).draft.inputs.rentalRegime).toBe('real');
  });
});

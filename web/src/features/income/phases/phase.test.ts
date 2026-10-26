import type { TFunction } from 'i18next';
import { describe, expect, it } from 'vitest';
import { z } from 'zod';
import { phase } from '../testData';
import {
  cadenceFromWire, cadenceToWire, checkPhase, followingPhase, groupPhases, newPhase, overlappingLines, phaseFieldOf,
  phaseChangeFieldOf, phaseChangeSchema, phaseChangeValuesOf, phaseSchema, phaseShape, phaseValuesOf, toWirePhase, toWirePhaseChange, type PhaseValues,
} from './phase';

const t = ((key: string) => key) as unknown as TFunction;
const line = (over: Partial<PhaseValues> = {}): PhaseValues => ({ ...newPhase('Salaire de base', '2025-01-01'), netAmount: '2300', ...over });
const issues = (v: PhaseValues) => {
  const r = phaseSchema(t).safeParse(v);
  return r.success ? [] : r.error.issues.map((i) => `${i.path.join('.')}: ${i.message}`);
};

describe('cadence', () => {
  it.each([['monthly', '', { count: 1, unit: 'month' }], ['quarterly', '', { count: 3, unit: 'month' }], ['yearly', '', { count: 1, unit: 'year' }], ['months', '6', { count: 6, unit: 'month' }]] as const)(
    '%s (%s) goes to the wire and back',
    (cadence, months, wire) => {
      expect(cadenceToWire(cadence, months)).toEqual(wire);
      expect(cadenceFromWire(wire)).toEqual({ cadence, months });
    },
  );
});

describe('a phase alone', () => {
  it('accepts a monthly phase with an amount typed with a comma', () => {
    expect(issues(line({ netAmount: '2300,50' }))).toEqual([]);
  });

  it('refuses an end on or before the start, a label of 61 characters, a zero net amount and N outside 2–12', () => {
    expect(issues(line({ endsOn: '2025-01-01' }))).toEqual(['endsOn: income_form.ends_after_start']);
    expect(issues(line({ label: 'l'.repeat(61) }))).toEqual(['label: income_form.label_too_long']);
    expect(issues(line({ netAmount: '0' }))).toEqual(['netAmount: investments_form.positive_amount_format']);
    expect(issues(line({ cadence: 'months', months: '13' }))).toEqual(['months: income_form.months_range']);
    expect(issues(line({ cadence: 'months', months: '1' }))).toEqual(['months: income_form.months_range']);
  });

  it('refuses a max of occurrences outside 1–1200 and a gross amount that is not positive', () => {
    expect(issues(line({ maxCycles: '0' }))).toEqual(['maxCycles: income_form.max_cycles_range']);
    expect(issues(line({ maxCycles: '1201' }))).toEqual(['maxCycles: income_form.max_cycles_range']);
    expect(issues(line({ grossAmount: '-1' }))).toEqual(['grossAmount: investments_form.positive_amount_format']);
  });

  it('refuses a taxable amount above the gross, or not positive; accepts it equal or without a gross', () => {
    expect(issues(line({ grossAmount: '5000', taxableAmount: '6000' }))).toEqual(['taxableAmount: income_form.taxable_above_gross']);
    expect(issues(line({ grossAmount: '5000', taxableAmount: '5000' }))).toEqual([]);
    expect(issues(line({ grossAmount: '', taxableAmount: '4400,00' }))).toEqual([]);
    expect(issues(line({ taxableAmount: '0' }))).toEqual(['taxableAmount: investments_form.positive_amount_format']);
  });

  it('sends the taxable amount when given, and keeps it through the form', () => {
    expect(toWirePhase(line({ grossAmount: '5500.00', taxableAmount: '4400,00' }), 'EUR'))
      .toMatchObject({ taxableAmount: { amount: '4400.00', currency: 'EUR' } });
    expect(toWirePhase(line(), 'EUR')).not.toHaveProperty('taxableAmount');
    expect(phaseValuesOf(phase({ taxableAmount: { amount: '4400.00', currency: 'EUR' } })).taxableAmount).toBe('4400.0');
    expect(phaseValuesOf(phase()).taxableAmount).toBe('');
  });

  it("places its issues under the line's path when it is a line of a larger form", () => {
    const schema = z.object({ phases: z.array(phaseShape(t)) })
      .superRefine((v, ctx) => v.phases.forEach((p, i) => checkPhase(p, ctx, t, ['phases', i])));
    const r = schema.safeParse({ phases: [line(), line({ endsOn: '2024-12-31' })] });
    expect(r.success ? [] : r.error.issues.map((i) => i.path.join('.'))).toEqual(['phases.1.endsOn']);
  });
});

describe('overlapping lines', () => {
  it('refuses the later of two lines of one label that overlap, never the earlier', () => {
    expect(overlappingLines([line(), line({ startsOn: '2025-06-01' })])).toEqual([1]);
  });

  it('lets two labels run in parallel, and a line start the day the previous one ends', () => {
    expect(overlappingLines([line(), line({ label: 'Prime annuelle', cadence: 'yearly' })])).toEqual([]);
    expect(overlappingLines([line({ endsOn: '2025-06-01' }), line({ startsOn: '2025-06-01' })])).toEqual([]);
  });

  it('compares labels trimmed, and leaves a line capped by its occurrences to the server', () => {
    expect(overlappingLines([line(), line({ label: ' Salaire de base ', startsOn: '2025-06-01' })])).toEqual([1]);
    expect(overlappingLines([line({ maxCycles: '3' }), line({ startsOn: '2025-06-01' })])).toEqual([]);
  });
});

describe('the wire', () => {
  it('sends the amounts as decimal strings in the source currency, and leaves out what is empty', () => {
    expect(toWirePhase(line({ netAmount: '2300,50' }), 'EUR')).toEqual({
      label: 'Salaire de base', startsOn: '2025-01-01', netAmount: { amount: '2300.50', currency: 'EUR' }, every: { count: 1, unit: 'month' },
    });
    expect(toWirePhase(line({ endsOn: '2025-12-31', grossAmount: '2900', maxCycles: '12', note: 'net après impôt' }), 'EUR')).toEqual({
      label: 'Salaire de base', startsOn: '2025-01-01', endsOn: '2025-12-31', netAmount: { amount: '2300', currency: 'EUR' },
      every: { count: 1, unit: 'month' }, grossAmount: { amount: '2900', currency: 'EUR' }, maxCycles: 12, note: 'net après impôt',
    });
  });

  it('maps a server path inside a phase to the field that owns it', () => {
    expect(phaseFieldOf('startsOn')).toBe('startsOn');
    expect(phaseFieldOf('netAmount.amount')).toBe('netAmount');
    expect(phaseFieldOf('every.count')).toBe('cadence');
    expect(phaseFieldOf('version')).toBeUndefined();
  });
});

describe('prefills', () => {
  it('reads a phase back into the form, its amounts trimmed', () => {
    expect(phaseValuesOf(phase({ grossAmount: { amount: '2900.00', currency: 'EUR' }, maxCycles: 12 }))).toEqual({
      label: 'Salaire de base', startsOn: '2025-01-01', endsOn: '', netAmount: '2300', cadence: 'monthly', months: '',
      grossAmount: '2900', taxableAmount: '', maxCycles: '12', note: '',
    });
  });

  it('starts a pay change without the phase’s taxable amount, which is the next payslip’s to give', () => {
    const p = phase({ grossAmount: { amount: '2900.00', currency: 'EUR' }, taxableAmount: { amount: '2750.00', currency: 'EUR' } });
    expect(phaseChangeValuesOf(p, '2026-11-01')).toMatchObject({ netAmount: '2300', grossAmount: '2900', taxableAmount: '' });
  });

  it('follows an ending phase: same label, starting on its end, same cadence and net amount', () => {
    const ending = phase({ endsOn: '2026-12-31', every: { count: 3, unit: 'month' }, netAmount: { amount: '900.00', currency: 'EUR' } });
    expect(followingPhase({ label: 'Salaire de base', endsOn: '2026-12-31' }, [ending])).toEqual({
      ...newPhase('Salaire de base', '2026-12-31'), netAmount: '900', cadence: 'quarterly',
    });
  });
});

describe('the table order', () => {
  it('groups by label in French order, then running, upcoming, past, newest first', () => {
    const groups = groupPhases([
      phase({ id: 'past', startsOn: '2024-01-01', status: 'past' }),
      phase({ id: 'prime', label: 'Prime annuelle' }),
      phase({ id: 'next', startsOn: '2026-01-01', status: 'upcoming' }),
      phase({ id: 'older', startsOn: '2023-01-01', status: 'past' }),
      phase({ id: 'now' }),
    ]);
    expect(groups.map((g) => [g.label, g.phases.map((p) => p.id)])).toEqual([
      ['Prime annuelle', ['prime']],
      ['Salaire de base', ['now', 'next', 'past', 'older']],
    ]);
  });
});

describe('the change of amount', () => {
  const change = { effectiveOn: '2026-11-01', netAmount: '2500', cadence: 'monthly' as const, months: '', grossAmount: '', taxableAmount: '', note: '' };

  it('sends the phase’s label, the date chosen and the new amounts', () => {
    expect(toWirePhaseChange('Salaire de base', { ...change, netAmount: '2500,50', grossAmount: '3200' }, 'EUR')).toEqual({
      label: 'Salaire de base', effectiveOn: '2026-11-01', netAmount: { amount: '2500.50', currency: 'EUR' },
      every: { count: 1, unit: 'month' }, grossAmount: { amount: '3200', currency: 'EUR' },
    });
  });

  it('asks for a date and N between 2 and 12, as a phase does', () => {
    const r = phaseChangeSchema(t).safeParse({ ...change, effectiveOn: '' });
    expect(r.success ? [] : r.error.issues.map((i) => i.path.join('.'))).toEqual(['effectiveOn']);
    const months = phaseChangeSchema(t).safeParse({ ...change, cadence: 'months', months: '13' });
    expect(months.success ? [] : months.error.issues.map((i) => `${i.path.join('.')}: ${i.message}`)).toEqual(['months: income_form.months_range']);
  });

  it('maps the server’s paths to the change’s fields; the label belongs to none', () => {
    expect(phaseChangeFieldOf('effectiveOn')).toBe('effectiveOn');
    expect(phaseChangeFieldOf('netAmount.amount')).toBe('netAmount');
    expect(phaseChangeFieldOf('every.count')).toBe('cadence');
    expect(phaseChangeFieldOf('label')).toBeUndefined();
  });
});

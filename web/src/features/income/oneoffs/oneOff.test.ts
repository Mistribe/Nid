import type { TFunction } from 'i18next';
import { describe, expect, it } from 'vitest';
import { oneOffSchema, oneOffValuesOf, toWireOneOff } from './oneOff';

const t = ((key: string) => key) as unknown as TFunction;
const issues = (v: { occurredOn: string; amount: string; note: string; taxableAmount?: string }) => {
  const r = oneOffSchema(t, '2026-10-07').safeParse({ taxableAmount: '', ...v });
  return r.success ? [] : r.error.issues.map((i) => `${i.path.join('.')}: ${i.message}`);
};

describe('a one-off', () => {
  it('accepts today, a past date and an amount with a comma', () => {
    expect(issues({ occurredOn: '2026-10-07', amount: '400,50', note: '' })).toEqual([]);
    expect(issues({ occurredOn: '2025-12-24', amount: '400', note: '' })).toEqual([]);
  });

  it('refuses tomorrow, a zero amount and a note of 2001 characters', () => {
    expect(issues({ occurredOn: '2026-10-08', amount: '400', note: '' })).toEqual(['occurredOn: field_error.core_date_in_future']);
    expect(issues({ occurredOn: '2026-10-07', amount: '0', note: '' })).toEqual(['amount: investments_form.positive_amount_format']);
    expect(issues({ occurredOn: '2026-10-07', amount: '1', note: 'n'.repeat(2001) })).toEqual(['note: investments_form.note_too_long']);
  });

  it('goes to the wire in the source’s currency, without an empty note, and comes back trimmed', () => {
    expect(toWireOneOff({ occurredOn: '2026-10-07', amount: '400,50', note: '', taxableAmount: '' }, 'EUR')).toEqual({ occurredOn: '2026-10-07', amount: { amount: '400.50', currency: 'EUR' } });
    expect(oneOffValuesOf({ id: 'o1', occurredOn: '2026-03-10', amount: { amount: '400.00', currency: 'EUR' }, note: null })).toEqual({ occurredOn: '2026-03-10', amount: '400', note: '', taxableAmount: '' });
  });

  it('takes an optional positive taxable amount to the wire and back', () => {
    expect(issues({ occurredOn: '2026-10-07', amount: '400', note: '', taxableAmount: '350,5' })).toEqual([]);
    expect(issues({ occurredOn: '2026-10-07', amount: '400', note: '', taxableAmount: '0' })).toEqual(['taxableAmount: investments_form.positive_amount_format']);
    expect(toWireOneOff({ occurredOn: '2026-10-07', amount: '400', note: '', taxableAmount: '350,50' }, 'EUR'))
      .toMatchObject({ taxableAmount: { amount: '350.50', currency: 'EUR' } });
    expect(oneOffValuesOf({ id: 'o1', occurredOn: '2026-03-10', amount: { amount: '400.00', currency: 'EUR' }, note: null, taxableAmount: { amount: '350.00', currency: 'EUR' } }).taxableAmount).toBe('350');
  });
});

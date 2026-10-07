import { describe, expect, it } from 'vitest';
import { defaultEffectiveDate, expectedRange, monthLabel, paymentDates, paymentWindow } from './months';

describe('expectedRange', () => {
  it.each([
    ['next12', '2026-10-07', { from: '2026-10-01', to: '2027-09-30' }],
    ['next12', '2026-01-31', { from: '2026-01-01', to: '2026-12-31' }],
    ['year', '2026-10-07', { from: '2026-01-01', to: '2026-12-31' }],
    ['next_year', '2026-10-07', { from: '2027-01-01', to: '2027-12-31' }],
  ] as const)('%s on %s', (preset, today, range) => {
    expect(expectedRange(preset, today)).toEqual(range);
  });
});

describe('paymentWindow', () => {
  it('covers the twelve months before this one, this one and the eleven after', () => {
    expect(paymentWindow('2026-10-07')).toEqual({ from: '2025-10-01', to: '2027-09-30' });
    expect(paymentWindow('2026-01-15')).toEqual({ from: '2025-01-01', to: '2026-12-31' });
  });

  it('ends a February on its last day, leap year or not', () => {
    expect(paymentWindow('2027-03-10').to).toBe('2028-02-29');
    expect(paymentWindow('2026-03-10').to).toBe('2027-02-28');
  });
});

describe('monthLabel', () => {
  it('reads a month in French, long or short', () => {
    expect(monthLabel(2026, 10)).toBe('octobre 2026');
    expect(monthLabel(2026, 10, 'short')).toBe('oct. 2026');
  });
});

const occurrence = (phaseId: string, on: string) => ({ phaseId, label: 'x', on, amount: { amount: '1', currency: 'EUR' } });
const answer = (...ons: ReturnType<typeof occurrence>[]) => ({
  id: 's', from: '2025-10-01', to: '2027-09-30',
  months: [{ year: 2026, month: 1, occurrences: ons, oneOffs: [], total: { amount: '0', currency: 'EUR' } }],
});

describe('paymentDates', () => {
  it('keeps the phase’s own dates, ascending, without its start', () => {
    const dates = paymentDates(answer(occurrence('p', '2026-03-01'), occurrence('q', '2026-02-15'), occurrence('p', '2026-01-01'), occurrence('p', '2026-02-01')),
      { id: 'p', startsOn: '2026-01-01' });
    expect(dates).toEqual(['2026-02-01', '2026-03-01']);
  });
});

describe('defaultEffectiveDate', () => {
  it('takes the first date on or after today, else the last one, else nothing', () => {
    expect(defaultEffectiveDate(['2026-09-01', '2026-10-01', '2026-11-01'], '2026-10-01')).toBe('2026-10-01');
    expect(defaultEffectiveDate(['2026-09-01', '2026-10-01', '2026-11-01'], '2026-10-07')).toBe('2026-11-01');
    expect(defaultEffectiveDate(['2026-08-01', '2026-09-01'], '2026-10-07')).toBe('2026-09-01');
    expect(defaultEffectiveDate([], '2026-10-07')).toBeUndefined();
  });
});

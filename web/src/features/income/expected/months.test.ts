import { describe, expect, it } from 'vitest';
import { expectedRange, monthLabel, paymentWindow } from './months';

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

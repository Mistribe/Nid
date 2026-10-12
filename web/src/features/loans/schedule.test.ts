import { describe, expect, it } from 'vitest';
import { initialYear, monthsOf, todaysRow } from './schedule';
import { rows, schedule, year } from './testData';

const { rows: plan, years } = schedule(); // 2025-11-15 to 2027-10-15

describe('todaysRow', () => {
  it('is the first payment due on or after today, today’s own included', () => {
    expect(todaysRow(plan, '2026-10-12')?.dueDate).toBe('2026-10-15');
    expect(todaysRow(plan, '2026-10-15')?.dueDate).toBe('2026-10-15');
    expect(todaysRow(plan, '2026-10-16')?.dueDate).toBe('2026-11-15');
  });

  it('is the first payment when today comes before it, and none once every payment is past', () => {
    expect(todaysRow(plan, '2020-01-01')?.number).toBe(1);
    expect(todaysRow(plan, '2027-10-16')).toBeNull();
  });
});

describe('initialYear', () => {
  it('is today’s calendar year when it has payments', () => {
    expect(initialYear(years, plan, '2026-10-12')).toBe(2026);
    expect(initialYear(years, plan, '2027-12-31')).toBe(2027); // the last payment is past, but the year has payments
  });

  it('is the year of the first payment when the loan starts after this year', () => {
    const later = rows('2027-01-15', 24);
    expect(initialYear([year({ year: 2027 }), year({ year: 2028 })], later, '2026-12-20')).toBe(2027);
  });

  it('is none once every payment is past and this year has none', () => {
    expect(initialYear(years, plan, '2028-03-01')).toBeNull();
  });
});

describe('monthsOf', () => {
  it('keeps one year’s payments, in order', () => {
    expect(monthsOf(plan, 2025).map((r) => r.dueDate)).toEqual(['2025-11-15', '2025-12-15']);
    expect(monthsOf(plan, 2026)).toHaveLength(12);
    expect(monthsOf(plan, 2030)).toEqual([]);
  });
});

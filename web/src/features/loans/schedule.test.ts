import { describe, expect, it } from 'vitest';
import { monthsOf } from './schedule';
import { schedule } from './testData';

const { rows: plan } = schedule(); // 2025-11-15 to 2027-10-15

describe('monthsOf', () => {
  it('keeps one year’s payments, in order', () => {
    expect(monthsOf(plan, 2025).map((r) => r.dueDate)).toEqual(['2025-11-15', '2025-12-15']);
    expect(monthsOf(plan, 2026)).toHaveLength(12);
    expect(monthsOf(plan, 2030)).toEqual([]);
  });
});

import { describe, expect, it } from 'vitest';
import { monthGrid, shiftMonth } from './monthGrid';

describe('monthGrid', () => {
  it('November_2026_starts_in_the_seventh_column', () => {
    const grid = monthGrid('2026-11');
    expect(grid).toHaveLength(42);
    expect(grid.slice(0, 7)).toEqual([
      ...['2026-10-26', '2026-10-27', '2026-10-28', '2026-10-29', '2026-10-30', '2026-10-31'].map((date) => ({ date, inMonth: false })),
      { date: '2026-11-01', inMonth: true },
    ]);
    expect(grid[35]).toEqual({ date: '2026-11-30', inMonth: true });
    expect(grid[41]).toEqual({ date: '2026-12-06', inMonth: false });
  });

  it('February_2027_fills_four_or_five_rows', () => {
    // 1 February 2027 is a Monday and 2027 is not a leap year: exactly four weeks.
    const grid = monthGrid('2027-02');
    expect(grid).toHaveLength(28);
    expect(grid[0]).toEqual({ date: '2027-02-01', inMonth: true });
    expect(grid[27]).toEqual({ date: '2027-02-28', inMonth: true });
    // 2028 is a leap year, 1 February 2028 a Tuesday: five weeks, the 29th in it.
    const leap = monthGrid('2028-02');
    expect(leap).toHaveLength(35);
    expect(leap[0]).toEqual({ date: '2028-01-31', inMonth: false });
    expect(leap).toContainEqual({ date: '2028-02-29', inMonth: true });
  });

  it('shiftMonth_crosses_years', () => {
    expect(shiftMonth('2026-01', -1)).toBe('2025-12');
    expect(shiftMonth('2025-12', 1)).toBe('2026-01');
    expect(shiftMonth('2026-11', 14)).toBe('2028-01');
    expect(shiftMonth('2026-03', -27)).toBe('2023-12');
  });
});

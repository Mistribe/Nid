import { describe, expect, it } from 'vitest';
import { contractYear, mondaysOf, monthsOf, rowsOf, startYears } from './gapWeeks';

describe('gapWeeks', () => {
  it('turns rows into Mondays and back, one row per run', () => {
    const mondays = mondaysOf([{ monday: '2026-12-21', weeks: '2' }, { monday: '2027-04-26', weeks: '1' }, { monday: '', weeks: '1' }]);
    expect([...mondays]).toEqual(['2026-12-21', '2026-12-28', '2027-04-26']);
    mondays.add('2027-01-04');
    expect(rowsOf(mondays)).toEqual([{ monday: '2026-12-21', weeks: '3' }, { monday: '2027-04-26', weeks: '1' }]);
  });

  it('a contract year runs to the day before its anniversary, its weeks grouped by the month of their Monday', () => {
    expect(contractYear('2028-02-29', 6)).toEqual({ from: '2028-02-29', to: '2029-02-27', allowed: 6 });
    const months = monthsOf(contractYear('2026-09-01', 6));
    expect(months[0]).toEqual({ month: '2026-09', mondays: ['2026-09-07', '2026-09-14', '2026-09-21', '2026-09-28'] });
    expect(months.at(-1)!.mondays.at(-1)).toBe('2027-08-30');
    expect(months.flatMap((m) => m.mondays)).toHaveLength(52);
  });

  it('a new arrangement plans its first two years once its start is known', () => {
    expect(startYears('', [{ weeks: '46' }])).toEqual([]);
    expect(startYears('2026-09-01', [{ weeks: '40' }, { weeks: '6' }])).toEqual([
      { from: '2026-09-01', to: '2027-08-31', allowed: 6 }, { from: '2027-09-01', to: '2028-08-31', allowed: 6 },
    ]);
  });
});

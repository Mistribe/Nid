import { describe, expect, it } from 'vitest';
import { clusters, offset, SLICES, years } from './timeline';

describe('the calendar timeline', () => {
  it('places a date between the ends', () => {
    expect(offset('2026-01-01', '2027-01-01', '2026-01-01')).toBe(0);
    expect(offset('2026-01-01', '2027-01-01', '2027-01-01')).toBe(1);
    expect(offset('2026-01-01', '2026-01-11', '2026-01-06')).toBe(0.5);
  });

  it('lists each new year within the horizon', () => {
    expect(years('2026-11-15', '2029-11-15').map((y) => y.year)).toEqual(['2027', '2028', '2029']);
    expect(years('2026-01-01', '2026-12-31')).toEqual([]);
  });

  it('groups the dates of one slice, keeps their order and places the group at the slice middle', () => {
    const dates = ['2026-01-02', '2026-01-03', '2030-06-01', '2035-12-31'];
    const groups = clusters(dates, '2026-01-01', '2036-01-01', (d) => d);
    expect(groups.map((g) => g.items)).toEqual([['2026-01-02', '2026-01-03'], ['2030-06-01'], ['2035-12-31']]);
    expect(groups[0]!.at).toBe(0.5 / SLICES);
    expect(groups[2]!.at).toBe((SLICES - 0.5) / SLICES); // the last day stays on the axis
  });
});

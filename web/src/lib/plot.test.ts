import { describe, expect, it } from 'vitest';
import { toPlot, yearTicks } from '@/lib/plot';

describe('toPlot', () => {
  it.each([['25000.0000', 25000], ['-120.5000', -120.5], ['0', 0], ['0.0001', 0.0001]])('%s → %s', (amount, n) => {
    expect(toPlot(amount)).toBe(n);
  });

  it('refuses a string that is not a decimal', () => {
    expect(() => toPlot('abc')).toThrow();
  });
});

describe('yearTicks', () => {
  it('gives each year once, from the first date then each 1 January (#718)', () => {
    const at = (iso: string) => Date.parse(iso);
    expect(yearTicks([at('2022-06-15'), at('2022-11-01'), at('2024-03-01'), at('2024-09-30')]).map((n) => new Date(n).toISOString().slice(0, 10)))
      .toEqual(['2022-06-15', '2023-01-01', '2024-01-01']);
    expect(yearTicks([])).toEqual([]);
  });
});

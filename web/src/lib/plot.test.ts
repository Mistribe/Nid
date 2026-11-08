import { describe, expect, it } from 'vitest';
import { toPlot } from '@/lib/plot';

describe('toPlot', () => {
  it.each([['25000.0000', 25000], ['-120.5000', -120.5], ['0', 0], ['0.0001', 0.0001]])('%s → %s', (amount, n) => {
    expect(toPlot(amount)).toBe(n);
  });

  it('refuses a string that is not a decimal', () => {
    expect(() => toPlot('abc')).toThrow();
  });
});

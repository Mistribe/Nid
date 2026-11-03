import { describe, expect, it } from 'vitest';
import { assumptionsBody, EMPTY } from './assumptionsBody';

describe('assumptionsBody', () => {
  it('sends percents as fractions by moving the point', () => {
    expect(assumptionsBody({ ...EMPTY, valueGrowth: '1,5', resaleFees: '10' })).toEqual({ valueGrowth: '0.015', resaleFees: '0.1' });
  });

  it('keeps a negative growth', () => {
    expect(assumptionsBody({ ...EMPTY, rentGrowth: '-0,5' })).toEqual({ rentGrowth: '-0.005' });
  });

  it('omits empty fields and sends counts as numbers', () => {
    expect(assumptionsBody({ ...EMPTY, yearsAfterPinel: '3', resalePrice: ' ' })).toEqual({ yearsAfterPinel: 3 });
  });
});

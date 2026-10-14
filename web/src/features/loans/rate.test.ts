import { describe, expect, it } from 'vitest';
import { fractionToPercent, isPercentRate, percentToFraction } from './rate';

describe('percentToFraction', () => {
  it.each([
    ['3,5', '0.035'], ['3.5', '0.035'], ['0,76', '0.0076'], ['0.76', '0.0076'], ['100', '1'], ['0', '0'],
    ['12,3456', '0.123456'], ['12.3456', '0.123456'], [' 4 ', '0.04'], ['003,50', '0.035'], ['0,0001', '0.000001'],
    ['99,9999', '0.999999'], ['150', '1.5'],
  ])('reads %j % as %j, moving the decimal point two places', (typed, wire) => {
    expect(percentToFraction(typed)).toBe(wire);
  });

  it.each(['12,34567', '0,00001', '-3,5', '-0', '+3', '', ' ', 'abc', '3,', ',5', '3,5,1', '3 5', '1e2', '3,5 %', '3,5%', '0x10'])(
    'refuses %j: a fifth decimal, a sign, or anything but digits and one separator', (typed) => {
      expect(percentToFraction(typed)).toBeNull();
    });
});

describe('fractionToPercent', () => {
  it.each([
    ['0.035', '3,5'], ['0.0076', '0,76'], ['1', '100'], ['0', '0'], ['0.123456', '12,3456'], ['0.04', '4'], ['0.5', '50'],
    ['0.000001', '0,0001'], ['1.5', '150'], ['-0.0120', '-1,2'], ['-0.5', '-50'], ['-0.0000', '0'], ['-0', '0'],
  ])('shows %j as %j %', (wire, shown) => {
    expect(fractionToPercent(wire)).toBe(shown);
  });

  it('goes back to what was typed, with a comma, never through a float', () => {
    for (const typed of ['3,5', '0,76', '12,3456', '0', '99,9999', '4', '0,0001'])
      expect(fractionToPercent(percentToFraction(typed)!)).toBe(typed);
  });

  it('leaves a value it cannot read as it came', () => {
    expect(fractionToPercent('abc')).toBe('abc');
    expect(fractionToPercent('--0.5')).toBe('--0.5');
  });
});

describe('isPercentRate', () => {
  it('accepts 0 up to 99,9999 and refuses 100 and above, a fifth decimal and garbage', () => {
    expect(['0', '0,0001', '3,5', '3.5', '99,9999'].map(isPercentRate)).toEqual([true, true, true, true, true]);
    expect(['100', '100,0', '150', '3,12345', '-1', '', 'abc'].map(isPercentRate)).toEqual([false, false, false, false, false, false, false]);
  });
});

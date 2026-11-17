import { describe, expect, it } from 'vitest';
import i18n from 'i18next';
import { partsText, signedPercentText } from './format';

describe('partsText', () => {
  it.each([['2.5', '2,5'], ['2.50', '2,5'], ['3.0', '3'], ['10', '10'], ['-0.5', '-0,5']])('shows %s parts as %s', (wire, shown) =>
    expect(partsText(wire)).toBe(shown));
});

describe('signedPercentText', () => {
  it.each([['5.1', '+5,1 %'], ['-2.7', '-2,7 %'], ['0', '0 %']])('shows %s as %s', (wire, shown) => expect(signedPercentText(i18n.t, wire)).toBe(shown));
});

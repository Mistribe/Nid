import { describe, expect, it } from 'vitest';
import { partsText } from './format';

describe('partsText', () => {
  it.each([['2.5', '2,5'], ['2.50', '2,5'], ['3.0', '3'], ['10', '10'], ['-0.5', '-0,5']])('shows %s parts as %s', (wire, shown) =>
    expect(partsText(wire)).toBe(shown));
});

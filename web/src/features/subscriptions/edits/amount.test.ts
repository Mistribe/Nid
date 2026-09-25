import { describe, expect, it } from 'vitest';
import { toWireAmount } from './amount';

describe('toWireAmount', () => {
  it.each([['15,99', '15.99'], [' 7.49 ', '7.49'], ['12', '12'], ['0,5', '0.5']])('writes %s as %s', (typed, wire) =>
    expect(toWireAmount(typed)).toBe(wire));
});

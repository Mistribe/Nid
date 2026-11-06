import { describe, expect, it } from 'vitest';
import { toFieldAmount, toWireAmount } from './amount';

describe('toWireAmount', () => {
  it.each([['15,99', '15.99'], [' 7.49 ', '7.49'], ['12', '12'], ['0,5', '0.5'], ['-120,50', '-120.50']])('writes %s as %s', (typed, wire) =>
    expect(toWireAmount(typed)).toBe(wire));
});

describe('toFieldAmount', () => {
  it.each([['32.05', '32,05'], ['200.00', '200,00'], ['12', '12'], ['-120.50', '-120,50']])('shows %s as %s', (wire, field) =>
    expect(toFieldAmount(wire)).toBe(field));
});

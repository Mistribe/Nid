import { describe, expect, it } from 'vitest';
import { toFieldAmount, toWireAmount } from '@/lib/amount';

describe('toWireAmount', () => {
  it.each([['15,99', '15.99'], [' 7.49 ', '7.49'], ['12', '12'], ['0,5', '0.5'], ['-120,50', '-120.50']])('writes %s as %s', (typed, wire) =>
    expect(toWireAmount(typed)).toBe(wire));
});

describe('toFieldAmount', () => {
  // #769: the wire's four decimals never reach a field (« 4500,0000 »), a rate keeps its significant digits.
  it.each([['32.05', '32,05'], ['200.00', '200,00'], ['12', '12'], ['-120.50', '-120,50'], ['21912.4200', '21912,42'], ['4500.0000', '4500,00'],
    ['0.0350', '0,035'], ['1.5', '1,5'], ['', '']])('shows %s as %s', (wire, field) =>
    expect(toFieldAmount(wire)).toBe(field));
});

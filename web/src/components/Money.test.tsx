import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { formatMoney, Money } from './Money';

const NNBSP = ' '; // French thousands separator
const NBSP = ' '; // before the currency sign

describe('formatMoney', () => {
  it('writes French amounts: thin space for thousands, comma, euro sign after', () =>
    expect(formatMoney({ amount: '1234.5', currency: 'EUR' })).toBe(`1${NNBSP}234,50${NBSP}€`));

  it('keeps every digit of an amount a binary double would round', () =>
    expect(formatMoney({ amount: '12345678901234567.89', currency: 'EUR' }))
      .toBe(`12${NNBSP}345${NNBSP}678${NNBSP}901${NNBSP}234${NNBSP}567,89${NBSP}€`));

  it('formats other currencies with their own sign', () =>
    expect(formatMoney({ amount: '10', currency: 'USD' })).toBe(`10,00${NBSP}$US`));

  it.each(['1e3', '12,50', '', ' 1', 'NaN'])('refuses %j, which is not a decimal string', (amount) =>
    expect(() => formatMoney({ amount, currency: 'EUR' })).toThrow(RangeError));
});

describe('<Money>', () => {
  it('renders the formatted amount, tabular only when asked', () => {
    render(<Money value={{ amount: '9.99', currency: 'EUR' }} tabular />);
    // collapseWhitespace: false — the default normalizer collapses the real NBSP in the DOM text to a
    // plain space before comparing, but leaves the (un-normalized) search string as-is, so the two never
    // match; disabling it compares the actual rendered text, NBSP included.
    const node = screen.getByText(`9,99${NBSP}€`, { collapseWhitespace: false });
    expect(node).toHaveClass('tabular-nums');
  });
});

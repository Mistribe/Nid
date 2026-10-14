/** A rate as typed in percent: digits, then a comma or a dot and at most four decimals (3B1 design §6). */
const TYPED = /^(\d+)(?:[.,](\d{1,4}))?$/;
/** A rate as the API writes it: an invariant decimal fraction (`0.035`). */
const WIRE = /^(-?)(\d+)(?:\.(\d+))?$/;

const withoutLeadingZeros = (digits: string) => digits.replace(/^0+(?=\d)/, '');

/**
 * The API's fraction for a rate typed in percent, by moving the decimal point two places in the text: `3,5` → `0.035`,
 * `0,76` → `0.0076`. No floating point touches it. Null when the text is not a rate as typed (a sign, a fifth decimal,
 * anything but digits and one separator); « below 100 % » is `isPercentRate`'s.
 */
export function percentToFraction(typed: string): string | null {
  const match = TYPED.exec(typed.trim());
  if (!match) return null;
  // The percentage times 10⁴, as an integer written in digits; the fraction is that times 10⁻⁶.
  const digits = withoutLeadingZeros(match[1]! + (match[2] ?? '').padEnd(4, '0')).padStart(7, '0');
  const whole = withoutLeadingZeros(digits.slice(0, -6));
  const decimals = digits.slice(-6).replace(/0+$/, '');
  return decimals ? `${whole}.${decimals}` : whole;
}

/** A rate of the API shown in percent with a decimal comma, the point moved back two places: `0.035` → `3,5`. */
export function fractionToPercent(wire: string): string {
  const match = WIRE.exec(wire);
  if (!match) return wire;
  const decimals = (match[3] ?? '').padEnd(2, '0');
  const whole = withoutLeadingZeros(match[2]! + decimals.slice(0, 2));
  const rest = decimals.slice(2).replace(/0+$/, '');
  const shown = rest ? `${whole},${rest}` : whole;
  return match[1] && /[1-9]/.test(shown) ? `-${shown}` : shown;
}

/** A rate as typed, at least 0 and below 100 % (the server's [0, 1) as a fraction). */
export function isPercentRate(typed: string): boolean {
  return /^0(?:\.|$)/.test(percentToFraction(typed) ?? '');
}

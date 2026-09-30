const DECIMAL = /^-?(0|[1-9]\d*)(\.\d+)?$/;

/**
 * An amount as a plotting coordinate: Recharts positions marks with numbers. This is the one place an amount becomes a
 * number (dataviz.md §1); nothing the person reads is derived from it — labels, the tooltip and the table show the
 * server's own strings.
 */
export function toPlot(amount: string): number {
  if (!DECIMAL.test(amount)) throw new RangeError(`Not a decimal amount: ${amount}`);
  // eslint-disable-next-line nid/no-amount-arithmetic -- plotting coordinates only
  return Number(amount);
}

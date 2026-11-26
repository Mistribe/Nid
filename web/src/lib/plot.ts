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

/**
 * Ticks for a time axis labelled by year (#718): the first time, then each 1 January up to the last, so a year shows once.
 * Times are UTC milliseconds, sorted.
 */
export function yearTicks(times: number[]): number[] {
  if (times.length === 0) return [];
  const first = new Date(times[0]!).getUTCFullYear();
  const last = new Date(times.at(-1)!).getUTCFullYear();
  return Array.from({ length: last - first + 1 }, (_, i) => (i === 0 ? times[0]! : Date.UTC(first + i, 0, 1)));
}

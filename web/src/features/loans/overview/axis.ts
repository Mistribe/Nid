/** The milliseconds of an ISO date at UTC midnight: a position on the timeline, never an amount. */
export const dayOf = (iso: string) => Date.UTC(Number(iso.slice(0, 4)), Number(iso.slice(5, 7)) - 1, Number(iso.slice(8, 10) || '1'));

/** The first day of the month after `yyyy-mm`, as an ISO date: where a step that ends in that month stops. */
export function monthAfter(month: string): string {
  const [y, m] = month.split('-').map(Number) as [number, number];
  return m === 12 ? `${y + 1}-01-01` : `${y}-${String(m + 1).padStart(2, '0')}-01`;
}

/** The same day `years` later (29 February falls to the 28th). */
export function yearsLater(iso: string, years: number): string {
  const y = Number(iso.slice(0, 4)) + years;
  const md = iso.slice(5) === '02-29' ? '02-28' : iso.slice(5);
  return `${y}-${md}`;
}

/** An axis between two dates: `at` places a date in [0, 1] (outside the axis, clamped), `ticks` the 1st of January between them. */
export function timeAxis(from: string, to: string) {
  const start = dayOf(from);
  const length = Math.max(1, dayOf(to) - start);
  const at = (iso: string) => Math.min(1, Math.max(0, (dayOf(iso) - start) / length));
  const first = Number(from.slice(0, 4)) + 1;
  const last = Number(to.slice(0, 4));
  const count = Math.max(0, last - first + 1);
  // At most about ten labels: one year in 1, 2 or 5.
  const every = count <= 10 ? 1 : count <= 20 ? 2 : 5;
  const ticks = Array.from({ length: count }, (_, i) => first + i).filter((y) => y % every === 0).map((y) => ({ year: y, at: at(`${y}-01-01`) }));
  return { at, ticks };
}

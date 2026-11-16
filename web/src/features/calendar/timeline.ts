// The calendar's timeline (#240): where a date sits on the axis, as `yyyy-mm-dd` strings. Dates only: no amount is computed here.

const dayOf = (iso: string) => Date.UTC(Number(iso.slice(0, 4)), Number(iso.slice(5, 7)) - 1, Number(iso.slice(8, 10)));

/** Where `date` sits between `from` (0) and `to` (1). */
export const offset = (from: string, to: string, date: string) => (dayOf(date) - dayOf(from)) / (dayOf(to) - dayOf(from));

/** The axis's slices: two marks in one slice would overlap, so they share a mark that opens as a list. */
export const SLICES = 20;

/** A row's events by slice of the axis, in the server's order, each group placed at its slice's middle. */
export function clusters<T>(items: readonly T[], from: string, to: string, dateOf: (item: T) => string): { at: number; items: T[] }[] {
  const bySlice = new Map<number, T[]>();
  for (const item of items) {
    const slice = Math.min(SLICES - 1, Math.max(0, Math.floor(offset(from, to, dateOf(item)) * SLICES)));
    bySlice.set(slice, [...(bySlice.get(slice) ?? []), item]);
  }
  return [...bySlice].map(([slice, list]) => ({ at: (slice + 0.5) / SLICES, items: list }));
}

/** Each 1st of January after `from` and up to `to`, where it sits. */
export function years(from: string, to: string): { year: string; at: number }[] {
  const out: { year: string; at: number }[] = [];
  for (let y = Number(from.slice(0, 4)) + 1; y <= Number(to.slice(0, 4)); y++) out.push({ year: String(y), at: offset(from, to, `${y}-01-01`) });
  return out;
}

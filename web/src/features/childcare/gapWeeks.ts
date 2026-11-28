import type { GapRow } from './childcareSchema';

/** A contract year: from an anniversary of the start to the day before the next (or the contract's end), with its weeks without care allowed. */
export type GapYear = { from: string; to: string; allowed: number };

const DAY = 86_400_000;
const time = (iso: string) => Date.parse(`${iso}T00:00:00Z`);
const iso = (t: number) => new Date(t).toISOString().slice(0, 10);
export const plusDays = (day: string, days: number) => iso(time(day) + days * DAY);

/** The contract year starting on `from`: to the day before its anniversary, 29 February falling on 28 February as on the server. */
export function contractYear(from: string, allowed: number): GapYear {
  const [y, m, d] = from.split('-').map(Number) as [number, number, number];
  const leap = m === 2 && d === 29 ? 28 : d;
  return { from, to: plusDays(iso(Date.UTC(y + 1, m - 1, leap)), -1), allowed };
}

/** Every Monday placed by the rows, a row being a first Monday and a number of weeks. */
export function mondaysOf(rows: GapRow[]): Set<string> {
  return new Set(rows.flatMap((r) => (/^\d{4}-\d{2}-\d{2}$/.test(r.monday) && Number(r.weeks) >= 1
    ? Array.from({ length: Number(r.weeks) }, (_, k) => plusDays(r.monday, 7 * k)) : [])));
}

/** The Mondays back as rows: one per run of consecutive weeks, in date order. */
export function rowsOf(mondays: Iterable<string>): GapRow[] {
  const rows: { monday: string; weeks: number }[] = [];
  for (const m of [...mondays].sort()) {
    const last = rows.at(-1);
    if (last && plusDays(last.monday, 7 * last.weeks) === m) last.weeks += 1;
    else rows.push({ monday: m, weeks: 1 });
  }
  return rows.map((r) => ({ monday: r.monday, weeks: String(r.weeks) }));
}

/** The year's weeks by the month of their Monday: a week counts in the contract year of its Monday (server rule). */
export function monthsOf(year: GapYear): { month: string; mondays: string[] }[] {
  const months: { month: string; mondays: string[] }[] = [];
  const first = plusDays(year.from, (8 - new Date(time(year.from)).getUTCDay()) % 7);
  for (let m = first; m <= year.to; m = plusDays(m, 7)) {
    const month = m.slice(0, 7);
    if (months.at(-1)?.month === month) months.at(-1)!.mondays.push(m);
    else months.push({ month, mondays: [m] });
  }
  return months;
}

/** The first two contract years of a new arrangement, each allowing 52 − the programmed weeks; none before its start is known. */
export function startYears(from: string, weekTypes: { weeks: string }[]): GapYear[] {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(from)) return [];
  const allowed = Math.max(0, 52 - weekTypes.reduce((sum, w) => sum + (Number(w.weeks) || 0), 0));
  const first = contractYear(from, allowed);
  return [first, contractYear(plusDays(first.to, 1), allowed)];
}

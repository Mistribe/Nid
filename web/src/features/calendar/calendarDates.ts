// Integer calendar arithmetic only: a local `Date` would shift the days in a time zone far from UTC.

type Cell = { date: string; inMonth: boolean };

const pad = (n: number, width = 2) => String(n).padStart(width, '0');
const iso = (y: number, m: number, d: number) => `${pad(y, 4)}-${pad(m)}-${pad(d)}`;
const parse = (month: string) => [Number(month.slice(0, 4)), Number(month.slice(5, 7))] as const;

const isLeap = (y: number) => (y % 4 === 0 && y % 100 !== 0) || y % 400 === 0;
export const daysIn = (y: number, m: number) => (m === 2 ? (isLeap(y) ? 29 : 28) : [4, 6, 9, 11].includes(m) ? 30 : 31);

/** Monday 0 … Sunday 6 (Sakamoto's method). */
function weekday(y: number, m: number, d: number): number {
  const t = [0, 3, 2, 5, 0, 3, 5, 1, 4, 6, 2, 4];
  const yy = m < 3 ? y - 1 : y;
  const sunday0 = (yy + Math.floor(yy / 4) - Math.floor(yy / 100) + Math.floor(yy / 400) + t[m - 1]! + d) % 7;
  return (sunday0 + 6) % 7;
}

/** `yyyy-mm` moved by `by` months. */
export function shiftMonth(month: string, by: number): string {
  const [y, m] = parse(month);
  const total = y * 12 + (m - 1) + by;
  return `${pad(Math.floor(total / 12), 4)}-${pad((total % 12) + 1)}`;
}

/** The month's weeks, Monday first, padded with the neighbouring months' days: 28, 35 or 42 cells. */
export function monthGrid(month: string): Cell[] {
  const [y, m] = parse(month);
  const [py, pm] = parse(shiftMonth(month, -1));
  const [ny, nm] = parse(shiftMonth(month, 1));
  const lead = weekday(y, m, 1);
  const days = daysIn(y, m);
  const cells: Cell[] = [];
  for (let i = lead; i > 0; i--) cells.push({ date: iso(py, pm, daysIn(py, pm) - i + 1), inMonth: false });
  for (let d = 1; d <= days; d++) cells.push({ date: iso(y, m, d), inMonth: true });
  for (let d = 1; cells.length % 7 !== 0; d++) cells.push({ date: iso(ny, nm, d), inMonth: false });
  return cells;
}

/** A calendar date (or a `yyyy-mm` month) as French text, read in UTC so the day never shifts. */
export const formatDate = (iso: string, options: Intl.DateTimeFormatOptions) =>
  new Intl.DateTimeFormat('fr-FR', { timeZone: 'UTC', ...options })
    .format(Date.UTC(Number(iso.slice(0, 4)), Number(iso.slice(5, 7)) - 1, Number(iso.slice(8, 10) || '1')));

/** The items grouped by day, in their given order. */
export function byDay<T>(items: readonly T[], dateOf: (item: T) => string): Map<string, T[]> {
  const days = new Map<string, T[]>();
  for (const item of items) days.set(dateOf(item), [...(days.get(dateOf(item)) ?? []), item]);
  return days;
}

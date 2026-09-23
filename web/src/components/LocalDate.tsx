const ISO_DATE = /^(\d{4})-(\d{2})-(\d{2})$/;

/**
 * A calendar date (yyyy-mm-dd) shown in UTC: `new Date("2026-09-19")` is UTC midnight and would show the
 * previous day west of Greenwich (frontend/architecture.md §11).
 */
export function formatLocalDate(iso: string, style: 'long' | 'short' = 'long', locale = 'fr-FR'): string {
  const match = ISO_DATE.exec(iso);
  if (!match) throw new RangeError(`Not an ISO date: ${JSON.stringify(iso)}`);
  const [, yearStr, monthStr, dayStr] = match;
  const [year, month, day] = [Number(yearStr), Number(monthStr), Number(dayStr)];
  const date = new Date(Date.UTC(year, month - 1, day));
  if (date.getUTCFullYear() !== year || date.getUTCMonth() !== month - 1 || date.getUTCDate() !== day)
    throw new RangeError(`Not a calendar date: ${iso}`);
  return new Intl.DateTimeFormat(locale, { timeZone: 'UTC', day: 'numeric', month: style, year: 'numeric' }).format(date);
}

export function LocalDate({ value, style = 'long' }: { value: string; style?: 'long' | 'short' }) {
  return <time dateTime={value}>{formatLocalDate(value, style)}</time>;
}

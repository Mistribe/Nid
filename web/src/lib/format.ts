export const percent = (value: number, sign = false) =>
  new Intl.NumberFormat('fr-FR', { maximumFractionDigits: 1, signDisplay: sign ? 'exceptZero' : 'auto' }).format(value);

const instantDay = new Intl.DateTimeFormat('fr-FR', { day: 'numeric', month: 'short', year: 'numeric' });
const instantTime = new Intl.DateTimeFormat('fr-FR', { hour: '2-digit', minute: '2-digit' });

/** A moment from the API (ISO 8601 with offset), in the reader's time zone: « 18 oct. 2026 à 14:18 ». */
export function formatInstant(iso: string): string {
  const date = new Date(iso);
  return `${instantDay.format(date)} à ${instantTime.format(date)}`;
}

const day = new Intl.DateTimeFormat('fr-FR', { dateStyle: 'long' });
/** The day of a moment from the API, in the reader's time zone (not the UTC day its text starts with). */
export const formatInstantDay = (iso: string): string => day.format(new Date(iso));

/** A calendar month, long (« octobre 2026 ») or short (« oct. 2026 »). */
export function monthLabel(year: number, month: number, style: 'long' | 'short' = 'long', locale = 'fr-FR'): string {
  return new Intl.DateTimeFormat(locale, { timeZone: 'UTC', month: style, year: 'numeric' }).format(new Date(Date.UTC(year, month - 1, 1)));
}

/** A server month (yyyy-MM) as « novembre 2040 ». */
export function yyyyMmLabel(yyyyMm: string): string {
  const [year, m] = yyyyMm.split('-').map(Number);
  return monthLabel(year ?? 0, m ?? 1, 'long');
}

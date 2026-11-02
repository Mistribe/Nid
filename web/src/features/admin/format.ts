const instant = new Intl.DateTimeFormat('fr-FR', { dateStyle: 'short', timeStyle: 'short' });
const decimal = new Intl.NumberFormat('fr-FR', { maximumFractionDigits: 1 });

/** A moment from the API (ISO 8601 with offset), in the reader's time zone. */
export const formatInstant = (iso: string): string => instant.format(new Date(iso));

const day = new Intl.DateTimeFormat('fr-FR', { dateStyle: 'long' });
/** The day of a moment from the API, in the reader's time zone (not the UTC day its text starts with). */
export const formatInstantDay = (iso: string): string => day.format(new Date(iso));

/** A size in bytes, as megabytes with one decimal (the admin area's only size: the database's). */
export const formatMegabytes = (bytes: number): string => decimal.format(bytes / 1_000_000);

/** A run's duration from the server's milliseconds: « 850 ms », « 12 s », « 3 min 05 s ». */
export function formatDuration(ms: number): string {
  if (ms < 1000) return `${ms} ms`;
  const seconds = Math.round(ms / 1000);
  if (seconds < 60) return `${seconds} s`;
  return `${Math.floor(seconds / 60)} min ${String(seconds % 60).padStart(2, '0')} s`;
}

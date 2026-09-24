const instant = new Intl.DateTimeFormat('fr-FR', { dateStyle: 'short', timeStyle: 'short' });
const decimal = new Intl.NumberFormat('fr-FR', { maximumFractionDigits: 1 });

/** A moment from the API (ISO 8601 with offset), in the reader's time zone. */
export const formatInstant = (iso: string): string => instant.format(new Date(iso));

/** A size in bytes, as megabytes with one decimal (the admin area's only size: the database's). */
export const formatMegabytes = (bytes: number): string => decimal.format(bytes / 1_000_000);

const decimal = new Intl.NumberFormat('fr-FR', { maximumFractionDigits: 1 });

/** A size in bytes, as megabytes with one decimal (the admin area's only size: the database's). */
export const formatMegabytes = (bytes: number): string => decimal.format(bytes / 1_000_000);

/** A run's duration from the server's milliseconds: « 850 ms », « 12 s », « 3 min 05 s ». */
export function formatDuration(ms: number): string {
  if (ms < 1000) return `${ms} ms`;
  const seconds = Math.round(ms / 1000);
  if (seconds < 60) return `${seconds} s`;
  return `${Math.floor(seconds / 60)} min ${String(seconds % 60).padStart(2, '0')} s`;
}

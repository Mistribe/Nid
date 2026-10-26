/** A server link (`/properties/{id}?tab=rental`) as a router destination: the path and its search parameters apart. */
export function parseNotificationLink(link: string): { to: string; search: Record<string, string> } {
  const url = new URL(link, 'http://nid.local');
  return { to: url.pathname, search: Object.fromEntries(url.searchParams) };
}

/** The calendar date, on the reader's clock, of an instant (`2026-10-20T23:30:00Z` is the 21st in Paris). */
export function localDateOf(instant: string): string {
  const at = new Date(instant);
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${at.getFullYear()}-${pad(at.getMonth() + 1)}-${pad(at.getDate())}`;
}

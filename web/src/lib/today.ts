/** Today as the browser sees it, as an ISO calendar date: compared as a string, never computed with. */
export function todayIso(now = new Date()): string {
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
}

/** The same calendar day `years` years back; 29 February lands on 1 March in a year without one. */
export function yearsAgoIso(years: number, now = new Date()): string {
  return todayIso(new Date(now.getFullYear() - years, now.getMonth(), now.getDate()));
}

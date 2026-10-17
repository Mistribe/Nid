/** A calendar date a month before today (yyyy-mm-dd), its day kept below 29 so the month after has it. */
export function aMonthAgo(): string {
  const now = new Date();
  return new Date(Date.UTC(now.getFullYear(), now.getMonth() - 1, Math.min(now.getDate(), 28))).toISOString().slice(0, 10);
}

/** The ISO date `k` months after `iso`; its day is below 29 (`aMonthAgo`), so every month has it. */
export function plusMonths(iso: string, k: number): string {
  const [y, m, d] = iso.split('-').map(Number) as [number, number, number];
  return new Date(Date.UTC(y, m - 1 + k, d)).toISOString().slice(0, 10);
}

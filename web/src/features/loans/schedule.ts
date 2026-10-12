import type { LoanScheduleRow, LoanScheduleYear } from './api';

/** Today's payment (spec §7): the first one due on or after today, ISO dates compared as strings; none once all are past. */
export function todaysRow(rows: LoanScheduleRow[], today: string): LoanScheduleRow | null {
  return rows.find((r) => r.dueDate >= today) ?? null;
}

/**
 * The year open at first (precision 7): today's calendar year when it has payments; otherwise the year of today's payment
 * (a loan whose payments start after this year); none once every payment is past.
 */
export function initialYear(years: LoanScheduleYear[], rows: LoanScheduleRow[], today: string): number | null {
  const current = Number(today.slice(0, 4));
  if (years.some((y) => y.year === current)) return current;
  const next = todaysRow(rows, today);
  return next ? Number(next.dueDate.slice(0, 4)) : null;
}

/** One year's payments, in order: the rows come sorted by due date. */
export function monthsOf(rows: LoanScheduleRow[], year: number): LoanScheduleRow[] {
  const prefix = `${year}-`;
  return rows.filter((r) => r.dueDate.startsWith(prefix));
}

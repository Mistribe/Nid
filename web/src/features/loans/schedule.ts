import type { LoanScheduleRow } from './api';

/** One year's payments, in order: the rows come sorted by due date. */
export function monthsOf(rows: LoanScheduleRow[], year: number): LoanScheduleRow[] {
  const prefix = `${year}-`;
  return rows.filter((r) => r.dueDate.startsWith(prefix));
}

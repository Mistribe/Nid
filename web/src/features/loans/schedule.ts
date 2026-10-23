/** One year's payments, in order: the rows come sorted by due date. */
export function monthsOf<T extends { dueDate: string }>(rows: T[], year: number): T[] {
  const prefix = `${year}-`;
  return rows.filter((r) => r.dueDate.startsWith(prefix));
}

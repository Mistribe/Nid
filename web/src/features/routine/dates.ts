/** 2E2 decision 1: the last day of the month before `today`'s (yyyy-mm-dd), as the statements are dated. */
export function defaultRoutineDate(today: string): string {
  const [year = 0, month = 1] = today.split('-').map(Number);
  const last = new Date(Date.UTC(year, month - 1, 0));
  return last.toISOString().slice(0, 10);
}

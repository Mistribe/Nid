import { vi } from 'vitest';

/**
 * Makes the local date getters read `hours` east of UTC, until the test's mocks are restored: vitest's worker threads hold a copy of
 * `process.env`, so assigning `process.env.TZ` in a test changes no zone.
 */
export function stubLocalDay(hours: number) {
  const shifted = (date: Date) => new Date(date.getTime() + hours * 3_600_000);
  vi.spyOn(Date.prototype, 'getFullYear').mockImplementation(function (this: Date) { return shifted(this).getUTCFullYear(); });
  vi.spyOn(Date.prototype, 'getMonth').mockImplementation(function (this: Date) { return shifted(this).getUTCMonth(); });
  vi.spyOn(Date.prototype, 'getDate').mockImplementation(function (this: Date) { return shifted(this).getUTCDate(); });
}

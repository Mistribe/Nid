import { yearsAgoIso } from '@/lib/today';

export const MAX_SERIES = 6;
export type Period = '1y' | '5y' | '10y' | 'all';

export const keysOf = (s: string | undefined): string[] => (s ? s.split(',') : []);
export const joinKeys = (keys: readonly string[]): string | undefined => (keys.length ? keys.join(',') : undefined);

const YEARS = { '1y': 1, '5y': 5, '10y': 10 } as const;
/** `from` of a period, looking back from today; « Tout » sends none. */
export const fromOf = (p: Period | undefined, now?: Date): string | undefined =>
  p === 'all' ? undefined : yearsAgoIso(YEARS[p ?? '5y'], now);

/** Why `unit` may not join a selection: three units or more read in base 100 only, which a percent has none of. */
export function blockOf(selectedUnits: readonly string[], unit: string): 'percent' | 'third_unit' | null {
  if (selectedUnits.includes(unit)) return null;
  const next = new Set([...selectedUnits, unit]);
  if (!next.has('percent') || next.size < 3) return null;
  return unit === 'percent' ? 'percent' : 'third_unit';
}

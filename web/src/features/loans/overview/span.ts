/**
 * Whole months from `from` to `to` (ISO dates): a gap between two dates for « dans 17 ans et 5 mois », never an amount.
 * A month counts once its day is reached; never negative.
 */
export function monthsBetween(from: string, to: string): number {
  const [fy, fm, fd] = from.split('-').map(Number) as [number, number, number];
  const [ty, tm, td] = to.split('-').map(Number) as [number, number, number];
  return Math.max(0, (ty - fy) * 12 + (tm - fm) - (td < fd ? 1 : 0));
}

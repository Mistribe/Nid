import type { SeriesPointsItem } from '@/api/series';
import { toPlot } from '@/lib/plot';

export type SeriesMode = 'value' | 'base100' | 'axes2';

/** One unit reads as it is; two read on two axes when one is a percent, else rebased; three or more are rebased. */
export function defaultMode(units: readonly string[]): SeriesMode {
  const distinct = [...new Set(units)];
  if (distinct.length <= 1) return 'value';
  return distinct.length === 2 && distinct.includes('percent') ? 'axes2' : 'base100';
}

/** `axes2`: the series of the first unit (in selection order) on the left axis, the other on the right. */
export function axisOf(series: readonly SeriesPointsItem[], mode: SeriesMode): ('left' | 'right')[] {
  return series.map((s) => (mode === 'axes2' && s.unit !== series[0]?.unit ? 'right' : 'left'));
}

/** A series with no base 100 (a percent) cannot be drawn rebased. */
export const drawable = (s: SeriesPointsItem, mode: SeriesMode): boolean =>
  mode !== 'base100' || s.points.some((p) => p.base100 != null);

export type SeriesRow = { at: number; date: string; plot: (number | null)[]; points: ({ value: string; base100?: string | null } | null)[] };

/** One row per distinct date, ascending; `plot` places the marks, `points` keeps the server's strings. */
export function seriesRows(series: readonly SeriesPointsItem[], mode: SeriesMode): SeriesRow[] {
  const dates = [...new Set(series.flatMap((s) => s.points.map((p) => p.date)))].sort();
  const byDate = series.map((s) => new Map(s.points.map((p) => [p.date, p])));
  return dates.map((date) => {
    const points = byDate.map((m) => m.get(date) ?? null);
    const plot = points.map((p) => {
      const text = mode === 'base100' ? p?.base100 : p?.value;
      return text == null ? null : toPlot(text);
    });
    return { at: Date.parse(date), date, plot, points };
  });
}

/** Per series, its last point on or before the row, only inside its own first..last range; the server's strings, picked not computed. */
export function pointsAt(rows: readonly SeriesRow[], index: number): SeriesRow['points'] {
  return (rows[0]?.points ?? []).map((_, i) => {
    const before = rows.slice(0, index + 1).findLast((r) => r.points[i]);
    return before && rows.slice(index).some((r) => r.points[i]) ? (before.points[i] ?? null) : null;
  });
}

import { describe, expect, it } from 'vitest';
import type { SeriesPointsItem } from '@/api/series';
import { axisOf, defaultMode, drawable, pointsAt, seriesRows } from './seriesRows';

const item = (unit: string, points: SeriesPointsItem['points'] = []): SeriesPointsItem => ({ key: unit, kind: 'cpi', unit, frequency: 'monthly', points });

describe('series rows', () => {
  it('picks one axis for one unit, a switchable base 100 for two, two axes when a percent is in, base 100 from three', () => {
    expect(defaultMode(['index'])).toBe('value');
    expect(defaultMode(['index', 'index'])).toBe('value');
    expect(defaultMode(['index', 'rate:EUR'])).toBe('base100');
    expect(defaultMode(['index', 'percent'])).toBe('axes2');
    expect(defaultMode(['index', 'rate:EUR', 'percent'])).toBe('base100');
  });

  it('puts the second unit on the right axis in axes2 only', () => {
    const series = [item('index'), item('percent'), item('index')];
    expect(axisOf(series, 'axes2')).toEqual(['left', 'right', 'left']);
    expect(axisOf(series, 'base100')).toEqual(['left', 'left', 'left']);
  });

  it('merges points by date, base 100 in base-100 mode', () => {
    const a = item('index', [{ date: '2025-01-01', value: '100', base100: '100' }, { date: '2025-04-01', value: '101', base100: '101' }]);
    const b = item('rate:EUR', [{ date: '2025-04-01', value: '0.86', base100: '100' }]);
    const rows = seriesRows([a, b], 'value');
    expect(rows).toHaveLength(2);
    expect(rows[0]?.plot).toEqual([100, null]);
    expect(rows[1]?.plot).toEqual([101, 0.86]);
    expect(seriesRows([a, b], 'base100')[1]?.plot).toEqual([101, 100]);
    expect(rows[1]?.points[1]).toMatchObject({ value: '0.86', base100: '100' });
  });

  it('marks a series without any base 100 as not drawable in base 100', () => {
    const s = item('percent', [{ date: '2025-01-01', value: '2.1', base100: null }]);
    expect(drawable(s, 'base100')).toBe(false);
    expect(drawable(s, 'value')).toBe(true);
  });

  it('reads each series at the hovered date: its last point so far, only inside its own range', () => {
    const monthly = item('index', [{ date: '2025-01-01', value: '100' }, { date: '2025-02-01', value: '101' }, { date: '2025-03-01', value: '102' }]);
    const quarterly = item('index', [{ date: '2025-01-01', value: '140' }, { date: '2025-03-01', value: '141' }]);
    const late = item('index', [{ date: '2025-02-01', value: '9' }]);
    const rows = seriesRows([monthly, quarterly, late], 'value');
    expect(pointsAt(rows, 1).map((p) => p?.value)).toEqual(['101', '140', '9']);
    expect(pointsAt(rows, 0).map((p) => p?.value)).toEqual(['100', '140', undefined]);
    expect(pointsAt(rows, 2).map((p) => p?.value)).toEqual(['102', '141', undefined]);
  });
});

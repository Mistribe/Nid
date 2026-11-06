import { describe, expect, it } from 'vitest';
import { eur, overview } from '../testData';
import { rowsOf } from './ValueChart';

describe('rowsOf', () => {
  it('stacks a kind not opened yet at 0, and leaves a point with an unknown total empty (#379)', () => {
    const d = overview({
      points: [
        { on: '2026-06-30', value: null, netContributions: null, byKind: [{ kind: 'pea', value: null }], paidIn: false },
        { on: '2026-07-31', value: eur('100.00'), netContributions: eur('90.00'), byKind: [{ kind: 'pea', value: eur('100.00') }], paidIn: false },
        { on: '2026-08-31', value: eur('150.00'), netContributions: eur('140.00'),
          byKind: [{ kind: 'pea', value: eur('110.00') }, { kind: 'per', value: eur('40.00') }], paidIn: false },
      ],
    });
    const rows = rowsOf(d, ['pea', 'per'], (p) => p.on);
    expect(rows.map((r) => [r.k_pea, r.k_per])).toEqual([[null, null], [100, 0], [110, 40]]);
    // The tooltip names only the kinds the point has.
    expect(Object.keys(rows[1]!.texts)).toEqual(['pea']);
  });
});

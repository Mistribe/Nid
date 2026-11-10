import { describe, expect, it } from 'vitest';
import { atEnd, atStart, dayOffset, extend, initialRange, lanes, months } from './timeline';

describe('frise › plage de mois', () => {
  it('lists the months of a range, both ends included', () => {
    expect(months({ from: '2026-11', to: '2027-01' })).toEqual(['2026-11', '2026-12', '2027-01']);
  });

  it('starts two months back and three ahead, never before the first charge nor after the current month for it', () => {
    expect(initialRange('2026-11', null)).toEqual({ from: '2026-11', to: '2027-02' });
    expect(initialRange('2026-11', '2025-01')).toEqual({ from: '2026-09', to: '2027-02' });
    expect(initialRange('2026-11', '2026-10')).toEqual({ from: '2026-10', to: '2027-02' });
    expect(initialRange('2026-11', '2027-03')).toEqual({ from: '2026-11', to: '2027-02' });
  });

  it('extends the past down to the first charge', () => {
    const range = { from: '2026-09', to: '2027-02' };
    expect(extend(range, 'past', '2026-11', '2026-08')).toEqual({ from: '2026-08', to: '2027-02' });
    expect(extend(range, 'past', '2026-11', '2025-01')).toEqual({ from: '2026-06', to: '2027-02' });
    expect(extend(range, 'past', '2026-11', null)).toBe(range);
    expect(atStart({ from: '2026-08', to: '2027-02' }, '2026-11', '2026-08')).toBe(true);
    expect(atStart(range, '2026-11', '2026-08')).toBe(false);
    expect(atStart({ from: '2026-11', to: '2027-02' }, '2026-11', null)).toBe(true);
  });

  it('extends the future up to twelve months ahead', () => {
    let range = { from: '2026-09', to: '2027-02' };
    for (let i = 0; i < 10; i++) range = extend(range, 'future', '2026-11', null);
    expect(range).toEqual({ from: '2026-09', to: '2027-11' });
    expect(atEnd(range, '2026-11')).toBe(true);
    expect(atEnd({ from: '2026-09', to: '2027-02' }, '2026-11')).toBe(false);
  });

  it('places a day from 0 (the 1st) to 1 (the last day)', () => {
    expect(dayOffset('2026-11-01')).toBe(0);
    expect(dayOffset('2026-11-30')).toBe(1);
    expect(dayOffset('2027-02-28')).toBe(1);
    expect(dayOffset('2026-11-16')).toBeCloseTo(15 / 29);
  });
});

describe('frise › lignes', () => {
  it('gives each subscription one lane, sorted by name, whichever month it appears in', () => {
    const c = (itemId: string, displayName: string) => ({ itemId, displayName });
    expect(lanes([
      { charges: [c('b', 'Spotify'), c('a', 'Électricité'), c('b', 'Spotify')] },
      { charges: [c('c', 'assurance'), c('a', 'Électricité')] },
    ]).map((l) => l.itemId)).toEqual(['c', 'a', 'b']);
    expect(lanes([])).toEqual([]);
  });
});

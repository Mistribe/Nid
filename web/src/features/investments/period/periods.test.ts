import { describe, expect, it } from 'vitest';
import { presetRange } from './periods';

describe('presetRange', () => {
  it('starts the year on 1 January', () => expect(presetRange('ytd', '2026-09-30')).toEqual({ from: '2026-01-01', to: '2026-09-30' }));
  it('goes back twelve months', () => expect(presetRange('last12', '2026-09-30')).toEqual({ from: '2025-09-30', to: '2026-09-30' }));
  it('turns 29 February into 28 February a year before', () => expect(presetRange('last12', '2028-02-29')).toEqual({ from: '2027-02-28', to: '2028-02-29' }));
  it('gives the whole previous year', () => expect(presetRange('previous_year', '2026-01-01')).toEqual({ from: '2025-01-01', to: '2025-12-31' }));
  it('on 1 January the year to date is that single day', () => expect(presetRange('ytd', '2026-01-01')).toEqual({ from: '2026-01-01', to: '2026-01-01' }));
});

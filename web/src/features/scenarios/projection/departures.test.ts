import { describe, expect, it } from 'vitest';
import { departurePath, departuresComplete, retirementsOf } from './departures';

const A = '0192f0aa-0000-7000-8000-0000000000a1';
const B = '0192f0aa-0000-7000-8000-0000000000b2';
const typed = (years: string, months: string, pension: string) => ({ years, months, pension });

describe('retirementsOf', () => {
  it('sends an age only when typed, months only with it, and a pension alone as just the pension', () => {
    expect(retirementsOf({ [A]: typed('', '', '1 500') })).toEqual([{ person: A, monthlyPension: '1500' }]);
    expect(retirementsOf({ [A]: typed('', '4', '') })).toEqual([]);
    expect(retirementsOf({ [A]: typed('60', '', '') })).toEqual([{ person: A, years: 60 }]);
    expect(retirementsOf({ [A]: typed('60', '6', '') })).toEqual([{ person: A, years: 60, months: 6 }]);
  });
});

describe('departuresComplete', () => {
  it('refuses months without an age', () => {
    expect(departuresComplete({ [A]: typed('', '4', '') })).toBe(false);
    expect(departuresComplete({ [A]: typed('60', '4', '') })).toBe(true);
  });
});

describe('departurePath', () => {
  it('names the field of the i-th sent adult', () => {
    const sent = [{ person: A, years: 60 }, { person: B, years: 50 }];
    expect(departurePath('retirements[1].years', sent)).toBe(`departures.${B}.years`);
    expect(departurePath('retirements[0].months', sent)).toBe(`departures.${A}.months`);
    expect(departurePath('retirements[1].monthlyPension', sent)).toBe(`departures.${B}.pension`);
    expect(departurePath('retirements[0]', sent)).toBe(`departures.${A}.years`);
    expect(departurePath('retirements[2].years', sent)).toBeUndefined();
    expect(departurePath('horizon', sent)).toBeUndefined();
  });
});

import { describe, expect, it } from 'vitest';
import { compareSearch } from './compareSearch';

const A = '0192f0aa-0000-7000-8000-0000000000a1';
const B = '0192f0aa-0000-7000-8000-0000000000b1';
const C = '0192f0aa-0000-7000-8000-0000000000c1';
const D = '0192f0aa-0000-7000-8000-0000000000d1';

describe('compareSearch', () => {
  it('keeps the first three distinct ids, in order', () => {
    expect(compareSearch({ ids: `${A},${B},${A},${C},${D}` })).toEqual({ ids: `${A},${B},${C}` });
  });

  it('drops parts that are no id, and ids when none remain', () => {
    expect(compareSearch({ ids: `junk,${B}` })).toEqual({ ids: B });
    expect(compareSearch({ ids: 'junk' })).toEqual({});
    expect(compareSearch({ ids: 42 })).toEqual({});
  });

  it('keeps 5, 20 and 30 years and a retirement, and leaves the 10-year default out', () => {
    expect(compareSearch({ horizon: '20y' })).toEqual({ horizon: '20y' });
    expect(compareSearch({ horizon: `retire:${A}` })).toEqual({ horizon: `retire:${A}` });
    expect(compareSearch({ horizon: '10y' })).toEqual({});
    expect(compareSearch({ horizon: '7y' })).toEqual({});
  });
});

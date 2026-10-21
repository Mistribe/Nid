import { describe, expect, it } from 'vitest';
import { band, spread, stack } from './flow';

describe('flow layout', () => {
  it('stacks the sizes over the height, gaps included', () => {
    expect(stack([{ size: 3 }, { size: 1 }], 108, 8)).toEqual([{ size: 3, y0: 0, y1: 75 }, { size: 1, y0: 83, y1: 108 }]);
  });

  it('draws nothing tall for sizes that sum to zero', () => {
    expect(stack([{ size: 0 }, { size: 0 }], 100, 0)).toEqual([{ size: 0, y0: 0, y1: 0 }, { size: 0, y0: 0, y1: 0 }]);
  });

  it('pushes crowded labels apart and keeps them inside the height', () => {
    expect(spread([50, 52, 54], 200, 18)).toEqual([50, 68, 86]);
    expect(spread([190, 195, 199], 200, 18)).toEqual([155, 173, 191]);
    expect(spread([0], 200, 18)).toEqual([9]);
  });

  it('joins two spans with a closed band', () => {
    expect(band(0, { y0: 0, y1: 10 }, 100, { y0: 20, y1: 30 })).toBe('M0 0C50 0,50 20,100 20L100 30C50 30,50 10,0 10Z');
  });
});

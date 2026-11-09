import { describe, expect, it } from 'vitest';
import { blockOf, fromOf, joinKeys, keysOf } from './selection';

describe('selection', () => {
  it('reads and writes the keys of the URL', () => {
    expect(keysOf(undefined)).toEqual([]);
    expect(keysOf('cpi,fx:EUR')).toEqual(['cpi', 'fx:EUR']);
    expect(joinKeys([])).toBeUndefined();
    expect(joinKeys(['cpi', 'irl'])).toBe('cpi,irl');
  });
  it('looks back from today only', () => {
    const now = new Date(2026, 10, 9);
    expect(fromOf(undefined, now)).toBe('2021-11-09');
    expect(fromOf('1y', now)).toBe('2025-11-09');
    expect(fromOf('10y', now)).toBe('2016-11-09');
    expect(fromOf('all', now)).toBeUndefined();
  });
  it('blocks a percent in a three-unit mix and a third unit beside a percent', () => {
    expect(blockOf(['index'], 'percent')).toBeNull();
    expect(blockOf(['index', 'rate:EUR'], 'percent')).toBe('percent');
    expect(blockOf(['index', 'percent'], 'rate:EUR')).toBe('third_unit');
    expect(blockOf(['index', 'rate:EUR'], 'currency:EUR')).toBeNull();
    expect(blockOf(['index', 'rate:EUR', 'percent'], 'index')).toBeNull();
    expect(blockOf([], 'percent')).toBeNull();
    expect(blockOf(['percent'], 'index')).toBeNull();
    expect(blockOf(['index', 'index'], 'percent')).toBeNull();
  });
});

import { describe, expect, it } from 'vitest';
import { yearsAgoIso } from './today';

describe('yearsAgoIso', () => {
  it('steps back whole years, 29 February landing on 1 March', () => {
    expect(yearsAgoIso(5, new Date(2028, 1, 29))).toBe('2023-03-01');
    expect(yearsAgoIso(5, new Date(2026, 10, 9))).toBe('2021-11-09');
  });
});

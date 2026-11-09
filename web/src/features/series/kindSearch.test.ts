import { describe, expect, it } from 'vitest';
import { kindsMatching } from './kindSearch';

describe('kindsMatching', () => {
  it('finds a kind by the French words of its label', () => {
    expect(kindsMatching('ipc')).toEqual(['cpi']);
    expect(kindsMatching('Loyer')).toEqual(['irl']);
    expect(kindsMatching('inflation')).toEqual(['cpi_yoy', 'projection_inflation']);
    expect(kindsMatching('projete')).toEqual(['projection_inflation', 'projection_rate']);
    expect(kindsMatching('obligations')).toEqual(['projection_rate']);
  });
  it('finds nothing for unknown or empty text', () => {
    expect(kindsMatching('zzz')).toEqual([]);
    expect(kindsMatching('  ')).toEqual([]);
  });
});

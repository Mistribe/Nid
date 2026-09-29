import { describe, expect, it } from 'vitest';
import { ownershipPath, ownershipToWire, percentToBp } from './ownership';

describe('ownership helpers', () => {
  it.each([['100', 10000], ['50', 5000], ['33,33', 3333], ['33.34', 3334], ['0', 0]] as const)('percentToBp(%s) = %s', (text, bp) => {
    expect(percentToBp(text)).toBe(bp);
  });

  it.each(['', 'abc', '101', '-1', '33,333'])('percentToBp(%s) is null', (text) => {
    expect(percentToBp(text)).toBeNull();
  });

  it('maps the form to the wire', () => {
    expect(ownershipToWire({ visibility: 'shared', owners: [{ personId: 'a', share: '60' }, { personId: 'b', share: '40' }] }))
      .toEqual({ visibility: 'shared', owners: [{ personId: 'a', shareBp: 6000 }, { personId: 'b', shareBp: 4000 }] });
  });

  it('maps server paths to fields', () => {
    expect(ownershipPath('owners[1].shareBp')).toBe('owners.1.share');
    expect(ownershipPath('visibility')).toBe('visibility');
    expect(ownershipPath('owners')).toBe('owners');
    expect(ownershipPath('displayName')).toBeUndefined();
  });
});

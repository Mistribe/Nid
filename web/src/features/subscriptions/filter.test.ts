import { describe, expect, it } from 'vitest';
import { labelParam, labelsOf } from './filter';

describe('label parameter', () => {
  it('reads and writes the comma-joined parameter, dropping blanks and duplicates', () => {
    expect(labelsOf(undefined)).toEqual([]);
    expect(labelsOf('vpn, ,vpn,streaming-video')).toEqual(['vpn', 'streaming-video']);
    expect(labelParam([])).toBeUndefined();
    expect(labelParam(['vpn', 'streaming-video'])).toBe('vpn,streaming-video');
  });
});

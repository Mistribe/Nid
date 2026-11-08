import { describe, expect, it } from 'vitest';
import { formatInstant } from '@/lib/format';

describe('formatInstant', () => {
  it('reads like the rest of the app: « 18 oct. 2026 à 14:18 »', () => {
    expect(formatInstant('2026-10-18T14:18:00+00:00')).toMatch(/^1[89] oct\. 2026 à \d{2}:18$/);
  });
});

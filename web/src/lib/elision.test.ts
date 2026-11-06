import { describe, expect, it } from 'vitest';
import { elision } from './elision';

describe('elision', () => {
  it('elides before a vowel only', () => {
    expect(['avril 2026', 'août 2026', 'octobre 2026'].map(elision)).toEqual(['vowel', 'vowel', 'vowel']);
    expect(['janvier 2026', 'septembre 2026', 'mai 2026'].map(elision)).toEqual([undefined, undefined, undefined]);
  });
});

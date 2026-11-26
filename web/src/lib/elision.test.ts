import { describe, expect, it } from 'vitest';
import { elision } from './elision';

describe('elision', () => {
  it('elides before a vowel only', () => {
    expect(['avril 2026', 'août 2026', 'octobre 2026'].map(elision)).toEqual(['vowel', 'vowel', 'vowel']);
    expect(['janvier 2026', 'septembre 2026', 'mai 2026'].map(elision)).toEqual([undefined, undefined, undefined]);
  });
});

describe('the « de » format', () => {
  it('elides « de » before a name starting with a vowel (#729)', async () => {
    const { default: i18n } = await import('@/app/i18n');
    expect(['Alice', 'Bob', 'Élise'].map((name) => i18n.t('dashboard.projection.retire', { name })))
      .toEqual(['Retraite d’Alice', 'Retraite de Bob', 'Retraite d’Élise']);
  });
});

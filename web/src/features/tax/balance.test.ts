import i18n from 'i18next';
import { describe, expect, it } from 'vitest';
import '@/locales/tax';
import { balanceWords, unsigned } from './balance';

describe('balanceWords', () => {
  it.each([['-2973.39', 'Remboursé en été 2027'], ['512.00', 'À payer en septembre 2027'], ['0.00', 'Rien à régler'], ['-0.00', 'Rien à régler']])(
    'says %s as %s', (balance, words) => expect(balanceWords(i18n.t, balance, 2026)).toBe(words));
  it('drops the minus sign', () => {
    expect(unsigned('-2973.39')).toBe('2973.39');
    expect(unsigned('512.00')).toBe('512.00');
  });
});

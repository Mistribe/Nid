import type { TFunction } from 'i18next';
import { ZERO } from './format';
import '@/locales/tax';

/** An amount without its leading minus: the sign is said in words (« Remboursé »), never shown. */
export const unsigned = (amount: string) => (amount.startsWith('-') ? amount.slice(1) : amount);

/** What the balance means, in words: it is settled the summer after the estimate's `year`. */
export function balanceWords(t: TFunction, balance: string, year: number): string {
  if (ZERO.test(balance)) return t('tax.balance.none');
  return t(balance.startsWith('-') ? 'tax.balance.refund' : 'tax.balance.to_pay', { year: year + 1 });
}

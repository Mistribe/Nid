/** What a person types (`15,99`, or `72 800` with a thousands space, U+00A0 or U+202F) as the API reads it (`15.99`): the text only, never a number (no arithmetic). */
export function toWireAmount(text: string): string {
  return text.replace(/\s/g, '').replace(',', '.');
}

/** `toWireAmount`'s counterpart: the server's amount (`32.05`) as a field shows it to a French reader (`32,05`). Text only. */
export const toFieldAmount = (amount: string) => amount.replace('.', ',');

/** An amount as typed: digits, then a comma or a dot and at most four decimals. */
export const AMOUNT = /^\d+([.,]\d{1,4})?$/;
export const ISO_DATE = /^\d{4}-\d{2}-\d{2}$/;
export const CURRENCY = /^[A-Z]{3}$/;

/** A value may be negative (an overdrawn current account); a flow never is — its kind gives the direction. */
export const SIGNED_AMOUNT = /^-?\d+([.,]\d{1,4})?$/;

/** A flow amount, and the opening's (finding 3): the server refuses zero (`core.amount_not_positive`), so the
 * client mirrors that bound rather than let a round trip say it. */
export const POSITIVE_AMOUNT = /^(?!0+(?:[.,]0+)?$)\d+([.,]\d{1,4})?$/;

/** Trailing zeros of the decimals only, string trimming — never arithmetic: `25000.0000` → `25000`, `12.50` → `12.5`. */
export function trimAmount(amount: string): string {
  return amount.includes('.') ? amount.replace(/0+$/, '').replace(/\.$/, '') : amount;
}

export type Direction = 'up' | 'down' | 'flat';

/** An amount's sign read from its decimal string, never parsed to a number. */
export function directionOf(amount: string): Direction {
  if (/^-/.test(amount)) return /^-0*(\.0*)?$/.test(amount) ? 'flat' : 'down';
  return /^0*(\.0*)?$/.test(amount) ? 'flat' : 'up';
}

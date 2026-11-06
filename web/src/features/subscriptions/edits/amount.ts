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

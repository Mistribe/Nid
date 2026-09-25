/** What a person types (`15,99`) as the API reads it (`15.99`): the text only, never a number (no arithmetic). */
export function toWireAmount(text: string): string {
  return text.trim().replace(',', '.');
}

/** An amount as typed: digits, then a comma or a dot and at most four decimals. */
export const AMOUNT = /^\d+([.,]\d{1,4})?$/;
export const ISO_DATE = /^\d{4}-\d{2}-\d{2}$/;
export const CURRENCY = /^[A-Z]{3}$/;

const BOM = String.fromCharCode(0xfeff);
const cell = (value: string) => (/[;"\r\n]/.test(value) ? `"${value.replaceAll('"', '""')}"` : value);

/** A server amount in a French spreadsheet: only the decimal point becomes a comma, nothing computed. */
export const csvAmount = (amount: string) => amount.replace('.', ',');

/** Rows for a spreadsheet: `;` separated (French Excel), CRLF, a BOM so it opens as UTF-8. */
export function csvText(rows: readonly (readonly string[])[]): string {
  return `${BOM}${rows.map((r) => r.map(cell).join(';')).join('\r\n')}\r\n`;
}

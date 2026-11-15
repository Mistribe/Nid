import type { TFunction } from 'i18next';
import { csvAmount, csvText } from '@/lib/csv';
import type { TaxDeclaration } from '@/api/tax';
import { boxText } from './format';
import '@/locales/tax';

/** The 2042's boxes for a spreadsheet, as the 2044's: the amounts are the server's strings, only the decimal point becomes a comma. */
export function declaration2042Csv(declaration: TaxDeclaration, personName: (person?: string | null) => string, t: TFunction): string {
  return csvText([
    [t('tax.d2042.csv_header.person'), t('tax.d2042.csv_header.box'), t('tax.d2042.csv_header.label'), t('tax.d2042.csv_header.amount')],
    ...declaration.boxes.map((b) => [personName(b.person), b.code, boxText(t, b.code), csvAmount(b.amount)]),
  ]);
}

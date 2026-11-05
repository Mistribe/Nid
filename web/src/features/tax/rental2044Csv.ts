import type { TFunction } from 'i18next';
import { csvAmount, csvText } from '../export/csv';
import type { Rental2044 } from './api';
import { deficitLines, lineText } from './format';
import '@/locales/tax';

/**
 * The 2044's lines for a spreadsheet: `;` separated (French Excel), CRLF, a BOM so it opens as UTF-8. The amounts are the
 * server's strings, only the decimal point becomes a comma.
 */
export function rental2044Csv(view: Rental2044, t: TFunction): string {
  const line = (code: string) => lineText(t, code);
  const { totals } = view;
  const [global, carried] = deficitLines(totals.interestDeficit);
  const household = t('tax.d2044.household');
  const rows: string[][] = [
    [t('tax.d2044.csv_header.property'), t('tax.d2044.csv_header.line'), t('tax.d2044.csv_header.label'), t('tax.d2044.csv_header.amount')],
    ...(view.regime === 'micro'
      ? [[household, '4BE', line('4BE'), totals.box4BE]]
      : [
        ...view.properties.flatMap((p) => p.lines.map((l) => [p.name, l.line, line(l.line), l.amount])),
        [household, '420', line('420'), totals.result],
        [household, global, line(global), totals.globalDeficit],
        [household, carried, line(carried), totals.carriedDeficit],
        [household, '4BA', line('4BA'), totals.box4BA],
      ]),
  ];
  return csvText(rows.map((r) => r.map((c, i) => (i === 3 ? csvAmount(c) : c))));
}

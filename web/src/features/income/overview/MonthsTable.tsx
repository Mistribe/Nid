import { useTranslation } from 'react-i18next';
import { Money } from '@/components/Money';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import type { IncomeOverview } from '../api';
import { monthLabel } from '@/lib/format';

type Amount = IncomeOverview['months'][number]['total'];

/** The chart's figures as the server gives them; a month without a total says which rate is missing. */
export function MonthsTable({ months, missingRates }: { months: IncomeOverview['months']; missingRates: string[] }) {
  const { t } = useTranslation();
  const cell = (v: Amount) => (v ? <Money value={v} tabular /> : '—');
  return (
    <Table aria-label={t('income.overview.months_table')}>
      <TableHeader>
        <TableRow>
          <TableHead>{t('income.overview.col_month')}</TableHead>
          <TableHead className="text-right">{t('income.overview.col_recurring')}</TableHead>
          <TableHead className="text-right">{t('income.overview.col_one_off')}</TableHead>
          <TableHead className="text-right">{t('income.overview.col_total')}</TableHead>
          <TableHead className="text-right">{t('income.overview.col_previous')}</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {months.map((m) => (
          <TableRow key={`${m.year}-${m.month}`}>
            <TableHead scope="row" className="font-normal">
              {monthLabel(m.year, m.month)}{m.upcoming ? ` · ${t('income.overview.upcoming')}` : ''}
            </TableHead>
            {m.total
              ? (
                <>
                  <TableCell className="text-right">{cell(m.recurring)}</TableCell>
                  <TableCell className="text-right">{cell(m.oneOff)}</TableCell>
                  <TableCell className="text-right font-medium">{cell(m.total)}</TableCell>
                </>
              )
              : <TableCell colSpan={3} className="text-right text-muted-foreground">{t('income.overview.missing_rate', { currencies: missingRates.join(', ') })}</TableCell>}
            <TableCell className="text-right">{cell(m.previousYear)}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}

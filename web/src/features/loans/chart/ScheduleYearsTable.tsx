import { useTranslation } from 'react-i18next';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import type { LoanScheduleYear } from '../api';
import { money } from '../format';
import '@/locales/loans';

/** « Afficher en tableau »: the same years, in the server's strings. */
export function ScheduleYearsTable({ years, currency }: { years: LoanScheduleYear[]; currency: string }) {
  const { t } = useTranslation();
  return (
    <Table aria-label={t('loan_detail.chart.table')}>
      <TableHeader>
        <TableRow>
          <TableHead>{t('loan_detail.chart.year')}</TableHead>
          <TableHead className="text-right">{t('loan_detail.chart.interest')}</TableHead>
          <TableHead className="text-right">{t('loan_detail.chart.principal')}</TableHead>
          <TableHead className="text-right">{t('loan_detail.chart.balance')}</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {years.map((y) => (
          <TableRow key={y.year}>
            <TableCell className="tabular-nums">{y.year}</TableCell>
            <TableCell className="text-right tabular-nums">{money(currency, y.interest)}</TableCell>
            <TableCell className="text-right tabular-nums">{money(currency, y.principal)}</TableCell>
            <TableCell className="text-right tabular-nums">{money(currency, y.balance)}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}

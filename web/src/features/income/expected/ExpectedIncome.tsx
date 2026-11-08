import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { LocalDate } from '@/components/LocalDate';
import { Money } from '@/components/Money';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import type { IncomeExpected, IncomeOneOff } from '@/api/income';
import { monthLabel } from '@/lib/format';
import '@/locales/income';

type Months = IncomeExpected['months'];

/** A last column with one cell per month, across the month's rows; `oneOff` adds to a one-off line's label. */
export type PayslipColumn = { header: string; cell: (month: Months[number]) => ReactNode; oneOff?: (oneOff: IncomeOneOff) => ReactNode };

/**
 * One row group per month, newest first: its total, then each payment and each one-off, newest first. Filtered on a
 * component (`label`), a month lists that component's payments only, without a total (the server's covers them all).
 */
export function ExpectedTable({ months, label, payslipColumn }: { months: Months; label?: string; payslipColumn?: PayslipColumn }) {
  const { t } = useTranslation();
  return (
    <Table aria-label={t('income_detail.expected.table')}>
      <TableHeader>
        <TableRow>
          <TableHead>{t('income_detail.expected.col_month')}</TableHead>
          <TableHead>{t('income_detail.expected.col_line')}</TableHead>
          <TableHead>{t('income_detail.expected.col_date')}</TableHead>
          <TableHead className="text-right">{t('income_detail.expected.col_amount')}</TableHead>
          {payslipColumn ? <TableHead>{payslipColumn.header}</TableHead> : null}
        </TableRow>
      </TableHeader>
      {[...months].reverse().map((m) => {
        const lines: [string, string, ReactNode][] = [
          ...m.occurrences.filter((o) => !label || o.label === label).map((o): [string, string, ReactNode] => [
            `${o.phaseId}-${o.on}`, o.on,
            <><TableCell>{o.label}</TableCell><TableCell><LocalDate value={o.on} /></TableCell><TableCell className="text-right"><Money value={o.amount} tabular /></TableCell></>,
          ]),
          ...(label ? [] : m.oneOffs).map((f): [string, string, ReactNode] => [
            f.id, f.occurredOn,
            <>
              <TableCell>{f.note ? t('income_detail.expected.one_off_note', { note: f.note }) : t('income_detail.expected.one_off')}{payslipColumn?.oneOff?.(f)}</TableCell>
              <TableCell><LocalDate value={f.occurredOn} /></TableCell>
              <TableCell className="text-right"><Money value={f.amount} tabular /></TableCell>
            </>,
          ]),
        ].sort((a, b) => (a[1] < b[1] ? 1 : a[1] > b[1] ? -1 : 0));
        const rows: [string, ReactNode][] = [
          ...(label ? [] : [['total', (
            <><TableCell className="font-medium">{t('income_detail.expected.total')}</TableCell><TableCell /><TableCell className="text-right font-medium"><Money value={m.total} tabular /></TableCell></>
          )] as [string, ReactNode]]),
          ...(lines.length === 0
            ? [['none', <TableCell colSpan={3} className="text-muted-foreground">{t('income_detail.expected.none')}</TableCell>] as [string, ReactNode]]
            : lines.map(([key, , cells]): [string, ReactNode] => [key, cells])),
        ];
        return (
          <TableBody key={`${m.year}-${m.month}`}>
            {rows.map(([key, cells], i) => (
              <TableRow key={key}>
                {i === 0 ? <TableHead scope="rowgroup" rowSpan={rows.length} className="align-top">{monthLabel(m.year, m.month)}</TableHead> : null}
                {cells}
                {i === 0 && payslipColumn ? <TableCell rowSpan={rows.length} className="align-top">{payslipColumn.cell(m)}</TableCell> : null}
              </TableRow>
            ))}
          </TableBody>
        );
      })}
    </Table>
  );
}

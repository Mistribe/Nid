import { Link, useParams } from '@tanstack/react-router';
import { useId, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import type { IncomeDetailSearch, RootSearch } from '@/app/router';
import { LocalDate } from '@/components/LocalDate';
import { Money } from '@/components/Money';
import { Card, CardAction, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Table, TableBody, TableCell, TableRow } from '@/components/ui/table';
import type { IncomeSourceOverview } from '../api';

/** Income dashboard §4: the last payments due (the server's five, newest first), and the way to the whole history. */
export function LastPayments({ items }: { items: IncomeSourceOverview['lastPayments'] }) {
  const { t } = useTranslation();
  return (
    <LastCard title={t('income_detail.last_payments.title')} tab="history" all={t('income_detail.last_payments.all')}>
      {items.length === 0 ? <p className="text-sm text-muted-foreground">{t('income_detail.last_payments.empty')}</p> : (
        <Table aria-label={t('income_detail.last_payments.title')}>
          <TableBody>
            {items.map((o) => (
              <TableRow key={`${o.phaseId}-${o.on}`}>
                <TableCell><LocalDate value={o.on} /></TableCell>
                <TableCell className="whitespace-normal">{o.label}</TableCell>
                <TableCell className="text-right"><Money value={o.amount} tabular /></TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
    </LastCard>
  );
}

/** A titled card of the overview's last items, with « Voir tout » to its tab and an optional action. */
export function LastCard({ title, tab, all, action, children }: { title: string; tab: string; all: string; action?: ReactNode; children: ReactNode }) {
  const titleId = useId();
  const { id } = useParams({ from: '/income/$id' });
  return (
    <Card role="region" aria-labelledby={titleId} className="gap-3 py-4">
      <CardHeader className="flex flex-wrap items-center gap-x-4 gap-y-2">
        <CardTitle id={titleId} className="mr-auto text-base">{title}</CardTitle>
        {action ? <CardAction className="static">{action}</CardAction> : null}
      </CardHeader>
      <CardContent className="space-y-2">
        {children}
        <Link from="/income/$id" to="/income/$id" params={{ id }} search={(prev: RootSearch & IncomeDetailSearch) => ({ ...prev, tab })} className="text-sm underline">{all}</Link>
      </CardContent>
    </Card>
  );
}

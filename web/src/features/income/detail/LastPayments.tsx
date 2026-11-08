import { Link, useParams } from '@tanstack/react-router';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import type { IncomeDetailSearch } from '@/app/router';
import type { RootSearch } from '@/lib/search';
import { LocalDate } from '@/components/LocalDate';
import { Money } from '@/components/Money';
import { seeAllClass } from '@/components/page/seeAll';
import { SectionCard } from '@/components/page/SectionCard';
import { Table, TableBody, TableCell, TableRow } from '@/components/ui/table';
import type { IncomeSourceOverview } from '../api';
import '@/locales/income';

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
  const { id } = useParams({ from: '/income/$id' });
  return (
    <SectionCard title={title} actions={action} className="space-y-2">
        {children}
        <Link from="/income/$id" to="/income/$id" params={{ id }} search={(prev: RootSearch & IncomeDetailSearch) => ({ ...prev, tab })} className={seeAllClass}>{all}</Link>
    </SectionCard>
  );
}

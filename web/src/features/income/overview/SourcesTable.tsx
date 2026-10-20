import type { LinkProps } from '@tanstack/react-router';
import { AlertTriangle, Archive } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { AccessBadge } from '@/components/AccessBadge';
import { formatLocalDate } from '@/components/LocalDate';
import { formatMoney } from '@/components/Money';
import { OwnerChips } from '@/components/OwnerChips';
import { SortableTable } from '@/components/page/SortableTable';
import { amountKey } from '@/components/page/sortKey';
import { percent } from '@/features/dashboard/format';
import type { IncomeOverview } from '../api';

type Row = IncomeOverview['sources'][number];

// Rents live on their property; its « Location » tab (id `rental`) holds the leases.
const link = (r: Row): LinkProps =>
  r.module === 'realestate'
    ? { to: '/properties/$id', params: { id: r.id }, search: { tab: 'rental' } }
    : { to: '/income/$id', params: { id: r.id } };

/** The overview's sources: monthly equivalent, change over a year, next payment and the end of the running period. */
export function SourcesTable({ rows }: { rows: Row[] }) {
  const { t } = useTranslation();
  const perMonth = (r: Row) => (r.perMonth ? formatMoney(r.perMonth) : '—');
  const change = (r: Row) => (r.change1y == null ? '—' : t('income.overview.pct', { value: percent(r.change1y, true) }));
  const next = (r: Row) => (r.nextOn && r.nextAmount ? t('income.overview.next_line', { date: formatLocalDate(r.nextOn, 'short'), amount: formatMoney(r.nextAmount) }) : '—');
  const gap = (r: Row) => r.gapOn
    ? <span role="note" className="inline-flex items-center gap-1 text-sm"><AlertTriangle aria-hidden="true" className="size-3.5 text-status-warning" />{t('income.overview.gap_on', { date: formatLocalDate(r.gapOn, 'short') })}</span>
    : null;
  return (
    <SortableTable<Row> caption={t('income.list')} rows={rows} rowKey={(r) => r.id} rowLink={link}
      initialSort={{ id: 'per_month', desc: true }}
      columns={[
        { id: 'name', header: t('lists.col.name'), sort: (r) => r.name, cell: (r) => <Name row={r} /> },
        { id: 'owner', header: t('income.overview.col_owners'), cell: (r) => <OwnerChips owners={r.owners} /> },
        { id: 'per_month', header: t('lists.col.per_month'), align: 'end', sort: (r) => amountKey(r.perMonth?.amount), cell: perMonth },
        { id: 'change', header: t('income.overview.col_1y'), align: 'end', sort: (r) => r.change1y ?? Number.NEGATIVE_INFINITY, cell: change },
        { id: 'next', header: t('income.overview.col_next'), sort: (r) => r.nextOn ?? '', cell: next },
        { id: 'end', header: t('income.overview.col_end'), cell: (r) => gap(r) ?? '—' },
      ]}
      mobile={(r) => (
        <div className="space-y-1">
          <div className="flex items-center justify-between gap-2"><Name row={r} /><OwnerChips owners={r.owners} /></div>
          <div className="flex flex-wrap items-center justify-between gap-2 text-sm">
            <span className="text-muted-foreground">{next(r)}</span>
            <span className="tabular-nums">{perMonth(r)} · {change(r)}</span>
          </div>
          {gap(r)}
        </div>
      )} />
  );
}

function Name({ row }: { row: Row }) {
  const { t } = useTranslation();
  return (
    <span className="inline-flex flex-wrap items-center gap-2">
      <span className="font-medium">{row.name}</span>
      {row.ended ? <span className="inline-flex items-center gap-1 text-sm text-muted-foreground"><Archive aria-hidden="true" className="size-4" />{t('income.ended_mark')}</span> : null}
      <AccessBadge access={row.visibility === 'private' ? 'private' : 'shared'} />
    </span>
  );
}

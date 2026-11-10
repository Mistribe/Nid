import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { useSession } from '@/app/Providers';
import type { InvestmentsReview as Review, InvestmentsReviewRow } from '@/api/reports';
import { Direction } from '@/components/Direction';
import { Money, type MoneyValue } from '@/components/Money';
import { Section } from '@/components/page/Section';
import { SortableTable } from '@/components/page/SortableTable';
import { amountKey } from '@/components/page/sortKey';
import { StatLine } from '@/components/page/StatLine';
import { pct } from '@/features/investments/Figures';
import { signed } from './NetWorthBridge';
import '@/locales/reports';

/** The year's investments: the server's gain, return and flows beside the year before, then one row per envelope. */
export function InvestmentsReview({ year, review }: { year: number; review: Review }) {
  const { t } = useTranslation();
  const { capabilities } = useSession();
  const unknown = <span className="text-muted-foreground">{t('reports.investments.unknown')}</span>;
  const money = (value?: MoneyValue | null): ReactNode => (value ? <Money value={value} tabular /> : unknown);
  const percent = (value?: number | null): ReactNode => (value == null ? unknown : t('reports.investments.pct', { value: pct(value) }));
  const before = (value: string) => t('reports.previous', { year: year - 1, amount: value });
  return (
    <Section title={t('reports.investments.title')}>
      <StatLine items={[
        { label: t('reports.investments.gain'), value: review.gain ? <Direction amount={review.gain} /> : unknown,
          hint: review.previousGain ? before(signed(review.previousGain)) : null },
        { label: t('reports.investments.return'), value: percent(review.periodReturnPct),
          hint: review.previousPeriodReturnPct == null ? null : before(t('reports.investments.pct', { value: pct(review.previousPeriodReturnPct) })) },
        { label: t('reports.investments.contributions'), value: money(review.contributions) },
        { label: t('reports.investments.withdrawals'), value: money(review.withdrawals) },
      ]} />
      <SortableTable<InvestmentsReviewRow> caption={t('reports.investments.envelopes')} rows={review.envelopes} rowKey={(r) => r.id}
        rowLink={capabilities.modules.includes('investments') ? (r) => ({ to: '/investments/$id', params: { id: r.id } }) : undefined}
        columns={[
          { id: 'name', header: t('reports.investments.col.name'), sort: (r) => r.name, cell: (r) => <Name row={r} /> },
          { id: 'start', header: t('reports.investments.col.start'), align: 'end', sort: (r) => amountKey(r.startValue?.amount), cell: (r) => money(r.startValue) },
          { id: 'end', header: t('reports.investments.col.end'), align: 'end', sort: (r) => amountKey(r.endValue?.amount), cell: (r) => money(r.endValue) },
          { id: 'net', header: t('reports.investments.col.net_contributions'), align: 'end', sort: (r) => amountKey(r.netContributions?.amount),
            cell: (r) => money(r.netContributions) },
          { id: 'gain', header: t('reports.investments.col.gain'), align: 'end', sort: (r) => amountKey(r.gain?.amount), cell: (r) => (r.gain ? signed(r.gain) : unknown) },
          { id: 'return', header: t('reports.investments.col.return'), align: 'end', sort: (r) => r.periodReturnPct ?? Number.NEGATIVE_INFINITY,
            cell: (r) => percent(r.periodReturnPct) },
        ]}
        mobile={(r) => (
          <div className="flex items-baseline justify-between gap-3">
            <Name row={r} />
            <span className="flex shrink-0 gap-3 tabular-nums"><span>{r.gain ? signed(r.gain) : unknown}</span><span>{percent(r.periodReturnPct)}</span></span>
          </div>
        )} />
    </Section>
  );
}

/** A start value older than the envelope's cadence, or one counted at the amount paid in, is said beside its name (as on the Performance page). */
function Name({ row }: { row: InvestmentsReviewRow }) {
  const { t } = useTranslation();
  return (
    <span className="inline-flex flex-wrap items-baseline gap-x-2">
      {row.name}
      {/* A space, so a screen reader does not run the name into the mark. */}
      {row.staleStart ? ' ' : null}
      {row.staleStart ? <span className="text-xs text-muted-foreground">{t('reports.investments.stale_start')}</span> : null}
      {row.noStartValue ? ' ' : null}
      {row.noStartValue ? <span className="text-xs text-muted-foreground">{t('reports.investments.no_start_value')}</span> : null}
    </span>
  );
}

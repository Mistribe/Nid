import { useTranslation } from 'react-i18next';
import { useSession } from '@/app/Providers';
import type { GoalReviewRow, GoalsReview as Review } from '@/api/reports';
import { formatLocalDate } from '@/components/LocalDate';
import { Money, type MoneyValue } from '@/components/Money';
import { Section } from '@/components/page/Section';
import { SortableTable } from '@/components/page/SortableTable';
import { amountKey } from '@/components/page/sortKey';
import { Dash, GoalProgress } from '@/features/goals/GoalStatus';
import '@/locales/goals';
import '@/locales/reports';

/**
 * The goals of the period: target, what was reached at both ends and the progress at the end; a goal created in the period has no
 * start (« — »), a reached amount without a rate is unknown.
 */
export function GoalsReview({ review }: { review: Review }) {
  const { t } = useTranslation();
  const { capabilities } = useSession();
  const money = (value?: MoneyValue | null) => (value ? <Money value={value} tabular /> : <Dash />);
  const end = (r: GoalReviewRow) => (r.endReached ? <Money value={r.endReached} tabular /> : <span className="text-muted-foreground">{t('reports.goals.unknown')}</span>);
  const name = (r: GoalReviewRow) => (
    <span className="inline-flex flex-wrap items-baseline gap-x-2">
      {r.name}
      {/* A space, so a screen reader does not run the name into the mark. */}
      {r.reachedInPeriod ? <> <span className="text-xs text-status-good-text">{t('reports.goals.reached_in_period')}</span></> : null}
      {r.archivedOn ? <> <span className="text-xs text-muted-foreground">{t('reports.goals.archived_on', { date: formatLocalDate(r.archivedOn) })}</span></> : null}
      {r.partial ? <> <span className="text-xs text-muted-foreground">{t('goals_list.partial')}</span></> : null}
    </span>
  );
  return (
    <Section title={t('reports.goals.title')}>
      <SortableTable<GoalReviewRow> caption={t('reports.goals.table')} rows={review.goals} rowKey={(r) => r.id}
        rowLink={capabilities.modules.includes('goals') ? (r) => ({ to: '/goals/$id', params: { id: r.id } }) : undefined}
        columns={[
          { id: 'name', header: t('reports.goals.col_name'), sort: (r) => r.name, cell: name },
          { id: 'target', header: t('goals_detail.target'), align: 'end', sort: (r) => amountKey(r.target?.amount), cell: (r) => money(r.target) },
          { id: 'start', header: t('reports.goals.col_start'), align: 'end', sort: (r) => amountKey(r.startReached?.amount), cell: (r) => money(r.startReached) },
          { id: 'end', header: t('reports.goals.col_end'), align: 'end', sort: (r) => amountKey(r.endReached?.amount), cell: end },
          { id: 'progress', header: t('goals_list.col_progress'), sort: (r) => r.endProgressPct ?? -1, cell: (r) => <GoalProgress pct={r.endProgressPct} /> },
        ]}
        mobile={(r) => <div className="flex items-baseline justify-between gap-3">{name(r)}<span className="tabular-nums">{end(r)}</span></div>} />
    </Section>
  );
}

import { lazy, Suspense, useState } from 'react';
import { useParams, useSearch } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { present, toProblem } from '@/api/errors';
import type { RootSearch } from '@/lib/search';
import { AccessBadge } from '@/components/AccessBadge';
import { ConfirmAction } from '@/components/ConfirmAction';
import { LocalDate } from '@/components/LocalDate';
import { Money } from '@/components/Money';
import { OwnerChips } from '@/components/OwnerChips';
import { DetailTabs, useDetailTab } from '@/components/page/DetailTabs';
import { PageHeader } from '@/components/page/PageHeader';
import { PageTrail } from '@/components/page/trail';
import { SharingBadges } from '@/components/ownership/SharingBadges';
import { SharingMenu } from '@/components/ownership/SharingMenu';
import { StatLine } from '@/components/page/StatLine';
import { ErrorPage } from '@/components/ErrorPage';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { useCloseGoal, useGoal, useReopenGoal, type GoalDetail, type GoalMoney } from '@/api/goals';
import { EditGoalDialog } from './EditGoalDialog';
import { CommitmentsTab } from './detail/CommitmentsTab';
import { EntriesTab } from './detail/EntriesTab';
import { FundingTab } from './detail/FundingTab';
import { kindText, presentGoalConflict } from './format';
import { Dash, GoalProgress, GoalStatus } from './GoalStatus';
import '@/locales/goals';
import '@/locales/deletion';
import { PageSkeleton } from '@/components/page/skeletons';

const DeleteItemFlow = lazy(() => import('@/components/DeleteItemFlow'));

// A pace of zero or less never reaches the target; read from the decimal text, nothing is computed.
const NOT_POSITIVE = /^(-|0+(\.0+)?$)/;
const NOTICES = { missing_currency: 'goals_detail.notice_missing_currency', closed_envelope: 'goals_detail.notice_closed_envelope', no_commitments: 'goals_list.no_commitments' } as const;

/**
 * A goal: its figures inline under the title (dashes where the server computed none), the notices its flags call for,
 * then one tab by kind: the funding envelopes, the free goal's readings, or the emergency fund's counted outflows. A reader
 * who cannot write it gets no write action.
 */
export function GoalDetailPage() {
  const { t } = useTranslation();
  const { id } = useParams({ from: '/goals/$id' });
  const { scope }: RootSearch = useSearch({ strict: false });
  const answer = useGoal(id, scope === 'me' ? 'me' : 'household');
  const [editing, setEditing] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const close = useCloseGoal();
  const reopen = useReopenGoal();
  const crumbs = [{ label: t('goals_list.title'), to: '/goals' as const }];
  if (answer.error) return <ErrorPage problem={present(toProblem(answer.error))} crumbs={crumbs} onRetry={() => void answer.refetch()} />;
  if (!answer.data) return <><PageTrail crumbs={crumbs} /><PageSkeleton label={t('goals_detail.loading')} actions={2} stats={4} tabs={2} /></>;

  const detail = answer.data;
  const goal = detail.goal;
  const pending = close.isPending || reopen.isPending;
  const error = close.error ?? reopen.error;
  const params = { params: { path: { id } } };
  const sharing = <SharingMenu item={id} onAnnounce={toast.success} shared={goal.visibility === 'shared'} label={t('page.more_actions')}
    trailing={[{ label: t('deletion.menu'), destructive: true, onSelect: () => setDeleting(true) }]} />;
  const actions = !goal.canWrite ? null : goal.closed
    ? <><Button type="button" variant="outline" disabled={pending} onClick={() => reopen.mutate(params)}>{t('goals_list.reopen')}</Button>{sharing}</>
    : <>
      <Button type="button" variant="outline" onClick={() => setEditing(true)}>{t('goals_form.edit')}</Button>
      <ConfirmAction label={t('goals_list.close')} confirmation={t('goals_list.close_confirmation', { name: goal.name })} pending={pending} onConfirm={() => close.mutate(params)} />
      {sharing}
    </>;
  const money = (m: GoalMoney | null | undefined) => m ? <Money value={m} tabular /> : <Dash />;
  // A reached goal has no date ahead, whatever its pace.
  const never = goal.status !== 'reached' && !goal.projectedOn && !!goal.monthlyPace && NOT_POSITIVE.test(goal.monthlyPace.amount);

  return (
    <article className="space-y-6">
      <PageHeader crumbs={crumbs} title={goal.name} actions={actions}
        meta={(
          <>
            <span className="text-sm text-muted-foreground">{kindText(t, goal.kind)}</span>
            <AccessBadge access={goal.visibility === 'private' ? 'private' : 'shared'} /><SharingBadges item={id} />
            {goal.closed ? <Badge variant="secondary">{t('goals_list.closed_badge')}</Badge> : null}
            <OwnerChips owners={goal.owners} />
          </>
        )}>
        <section aria-label={t('goals_detail.figures')}>
          <StatLine items={[
            { label: t('goals_detail.reached'), value: money(goal.reached) },
            { label: t('goals_detail.target'), value: money(goal.target) },
            { label: t('goals_list.progress'), value: <GoalProgress pct={goal.progressPct} /> },
            { label: t('goals_list.col_monthly'), value: money(goal.monthlyNeeded) },
            { label: t('goals_detail.monthly_pace'), value: money(goal.monthlyPace) },
            { label: t('goals_detail.projected'), value: goal.projectedOn ? <LocalDate value={goal.projectedOn} style="short" /> : never ? t('goals_detail.never') : <Dash /> },
            { label: t('goals_list.col_status'), value: <GoalStatus status={goal.status} /> },
          ]} />
        </section>
      </PageHeader>
      {goal.canWrite ? null : <p>{t('goals_list.read_only')}</p>}
      {deleting ? <Suspense fallback={null}><DeleteItemFlow kind="goals.delete_goal" id={id} onClose={() => setDeleting(false)} /></Suspense> : null}
      {editing ? <EditGoalDialog goalId={id} onClose={() => setEditing(false)} onDone={(message) => { setEditing(false); toast.success(message); }} /> : null}
      {error ? <ProblemMessage problem={presentGoalConflict(t, error)} /> : null}
      <ul className="space-y-1 text-sm">
        {goal.partial ? <li className="text-muted-foreground">{t('goals_list.partial')}</li> : null}
        {(Object.keys(NOTICES) as (keyof typeof NOTICES)[]).filter((flag) => goal.flags.includes(flag)).map((flag) => <li key={flag} className="text-status-warning-text">{t(NOTICES[flag])}</li>)}
      </ul>
      {detail.note ? <p>{detail.note}</p> : null}
      <Tabs detail={detail} />
    </article>
  );
}

/**
 * Tabs by content: the funding envelopes (a partial goal is funded even when none is visible), or else the readings; an
 * emergency fund adds its counted outflows.
 */
function Tabs({ detail }: { detail: GoalDetail }) {
  const { t } = useTranslation();
  const { goal } = detail;
  const first = detail.funding.length > 0 || goal.partial
    ? { id: 'funding', label: t('goals_detail.tab_funding'), content: <FundingTab funding={detail.funding} /> }
    : { id: 'entries', label: t('goals_detail.tab_entries'), content: <EntriesTab goalId={goal.id} entries={detail.entries} writable={goal.canWrite && !goal.closed} /> };
  const tabs = [first, ...(goal.kind === 'emergency_fund'
    ? [{ id: 'commitments', label: t('goals_detail.tab_commitments'), content: <CommitmentsTab commitments={detail.commitments} months={detail.months} /> }] : [])];
  const [tab, setTab] = useDetailTab(tabs.map((x) => x.id));
  return <DetailTabs value={tab} onChange={setTab} tabs={tabs} />;
}

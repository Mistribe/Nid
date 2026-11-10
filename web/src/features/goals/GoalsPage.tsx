import { Link, useSearch } from '@tanstack/react-router';
import { lazy, Suspense, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { present, toProblem } from '@/api/errors';
import type { RootSearch } from '@/lib/search';
import { EmptyState } from '@/components/EmptyState';
import { LocalDate } from '@/components/LocalDate';
import { Money } from '@/components/Money';
import { OwnerChips } from '@/components/OwnerChips';
import { DetailTabs, useDetailTab } from '@/components/page/DetailTabs';
import { PageHeader } from '@/components/page/PageHeader';
import { RowMenu } from '@/components/page/RowMenu';
import { SortableTable } from '@/components/page/SortableTable';
import { amountKey } from '@/components/page/sortKey';
import { ProblemMessage } from '@/components/ProblemMessage';
import { buttonVariants } from '@/components/ui/button';
import { useCloseGoal, useGoals, useReopenGoal, type GoalSummary } from '@/api/goals';
import { Amounts, Dash, GoalProgress, GoalStatus } from './GoalStatus';
import { presentGoalConflict, statusText } from './format';
import '@/locales/goals';
import '@/locales/forms';
import '@/locales/deletion';
import { Loading, TableSkeleton } from '@/components/page/skeletons';

const ConfirmDialog = lazy(() => import('@/components/ConfirmDialog'));
const DeleteItemFlow = lazy(() => import('@/components/DeleteItemFlow'));

const TABS = ['open', 'closed'] as const;

/** The goals list: open goals first, closed ones under their own tab (server order kept until a column is sorted). */
export function GoalsPage() {
  const { t } = useTranslation();
  const { scope }: RootSearch = useSearch({ strict: false });
  const [tab, setTab] = useDetailTab(TABS);
  // The empty state's action repeats the header's primary: `outline`, one primary per view (design-system.md §6.1).
  const add = (variant?: 'outline') => <Link to="/goals/new" className={buttonVariants({ variant })}>{t('goals_list.add')}</Link>;
  const scoped = scope === 'me' ? 'me' : 'household';
  return (
    <div className="space-y-4">
      <PageHeader title={t('goals_list.title')} actions={add()} />
      <DetailTabs value={tab} onChange={setTab} tabs={[
        { id: 'open', label: t('goals_list.tab_open'), content: <List scope={scoped} closed={false} add={add('outline')} /> },
        { id: 'closed', label: t('goals_list.tab_closed'), content: <List scope={scoped} closed add={add('outline')} /> },
      ]} />
    </div>
  );
}

function List({ scope, closed, add }: { scope: 'me' | 'household'; closed: boolean; add: React.ReactNode }) {
  const { t } = useTranslation();
  const answer = useGoals(scope, closed);
  if (answer.error) return <ProblemMessage problem={present(toProblem(answer.error))} />;
  if (!answer.data)
    return (
      <Loading label={t('goals_list.loading')}><TableSkeleton columns={8} /></Loading>
    );
  // `closed=true` lists the open goals too: this tab keeps the closed ones.
  const rows = closed ? answer.data.items.filter((g) => g.closed) : answer.data.items;
  if (rows.length === 0)
    return closed
      ? <EmptyState module="/goals" title={t('goals_list.empty_closed_title')} body={t('goals_list.empty_closed_body')} />
      : <EmptyState module="/goals" title={t('goals_list.empty_title')} body={t('goals_list.empty_body')} action={<>
        {add}
        {/* Issue #412: an emergency fund needs no amount, only months; the creation form shows the server's target. */}
        <Link to="/goals/new" search={{ kind: 'emergency_fund', months: 3 }} className={buttonVariants({ variant: 'outline' })}>{t('goals_list.add_emergency')}</Link>
      </>} />;

  const due = (g: GoalSummary) => g.targetDate ? <LocalDate value={g.targetDate} style="short" /> : <Dash />;
  const monthly = (g: GoalSummary) => g.monthlyNeeded ? <Money value={g.monthlyNeeded} tabular /> : <Dash />;
  // A reader who cannot write the goal gets no menu (close and reopen are writes).
  const actions = (g: GoalSummary) => g.canWrite ? <Actions goal={g} /> : null;
  return (
    <SortableTable<GoalSummary> caption={t('goals_list.list')} rows={rows} rowKey={(g) => g.id}
      columns={[
        { id: 'name', header: t('lists.col.name'), sort: (g) => g.name, cell: (g) => <Name goal={g} /> },
        { id: 'owner', header: t('goals_list.col_owners'), cell: (g) => <OwnerChips owners={g.owners} /> },
        { id: 'progress', header: t('goals_list.col_progress'), sort: (g) => g.progressPct ?? Number.NaN, cell: (g) => <GoalProgress pct={g.progressPct} /> },
        { id: 'amounts', header: t('goals_list.col_amounts'), align: 'end', sort: (g) => amountKey(g.reached?.amount), cell: (g) => <Amounts goal={g} /> },
        { id: 'due', header: t('goals_list.col_due'), sort: (g) => g.targetDate ?? '', cell: due },
        { id: 'monthly', header: t('goals_list.col_monthly'), align: 'end', sort: (g) => amountKey(g.monthlyNeeded?.amount), cell: monthly },
        { id: 'status', header: t('goals_list.col_status'), sort: (g) => (g.status ? statusText(t, g.status) : ''), cell: (g) => <GoalStatus status={g.status} /> },
        { id: 'menu', header: t('goals_list.col_actions'), align: 'end', cell: actions },
      ]}
      mobile={(g) => (
        <div className="space-y-1">
          <div className="flex items-center justify-between gap-2"><Name goal={g} /><span className="flex items-center gap-2"><OwnerChips owners={g.owners} />{actions(g)}</span></div>
          <div className="flex flex-wrap items-center justify-between gap-2 text-sm"><GoalProgress pct={g.progressPct} /><GoalStatus status={g.status} /></div>
          <div className="flex flex-wrap items-center justify-between gap-2 text-sm text-muted-foreground">
            <Amounts goal={g} />
            <span className="inline-flex gap-2"><span>{t('goals_list.col_due')} {due(g)}</span><span>{t('goals_list.col_monthly')} {monthly(g)}</span></span>
          </div>
        </div>
      )} />
  );
}

/** The name links to the detail; a partial view says so, without saying which envelope is hidden. */
function Name({ goal }: { goal: GoalSummary }) {
  const { t } = useTranslation();
  return (
    <span className="inline-flex flex-col">
      <Link to="/goals/$id" params={{ id: goal.id }} className="font-medium hover:underline">{goal.name}</Link>
      {goal.partial ? <span className="text-sm text-muted-foreground">{t('goals_list.partial')}</span> : null}
      {goal.flags.includes('no_commitments') ? <span className="text-sm text-muted-foreground">{t('goals_list.no_commitments')}</span> : null}
    </span>
  );
}

function Actions({ goal }: { goal: GoalSummary }) {
  const { t } = useTranslation();
  const [asking, setAsking] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const close = useCloseGoal();
  const reopen = useReopenGoal();
  const error = close.error ?? reopen.error;
  // A second click while a write is in flight would send it twice.
  const pending = close.isPending || reopen.isPending;
  return (
    <span className="flex flex-wrap items-center justify-end gap-2">
      <RowMenu label={t('goals_list.row_menu', { name: goal.name })} items={[goal.closed
        ? { label: t('goals_list.reopen'), onSelect: () => { if (!pending) reopen.mutate({ params: { path: { id: goal.id } } }); } }
        : { label: t('goals_list.close'), onSelect: () => { if (!pending) setAsking(true); } },
        ...(goal.canWrite ? [{ separator: true as const }, { label: t('deletion.menu'), destructive: true, onSelect: () => setDeleting(true) }] : [])]} />
      {deleting ? <Suspense fallback={null}><DeleteItemFlow kind="goals.delete_goal" id={goal.id} onClose={() => setDeleting(false)} /></Suspense> : null}
      {asking ? (
        <Suspense fallback={null}>
          <ConfirmDialog label={t('goals_list.close')} confirmation={t('goals_list.close_confirmation', { name: goal.name })} pending={close.isPending}
            onClose={() => setAsking(false)} onConfirm={() => { setAsking(false); close.mutate({ params: { path: { id: goal.id } } }); }} />
        </Suspense>
      ) : null}
      {error ? <ProblemMessage problem={presentGoalConflict(t, error)} /> : null}
    </span>
  );
}

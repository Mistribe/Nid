import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import { lazy, Suspense, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { present, toProblem } from '@/api/errors';
import type { GoalsSearch } from '@/app/router';
import type { RootSearch } from '@/lib/search';
import { EmptyState } from '@/components/EmptyState';
import { LocalDate } from '@/components/LocalDate';
import { Money } from '@/components/Money';
import { OwnerChips } from '@/components/OwnerChips';
import { DetailTabs, useDetailTab } from '@/components/page/DetailTabs';
import { foldByOwners, type Folded } from '@/components/page/foldByOwners';
import { PageHeader } from '@/components/page/PageHeader';
import { RowMenu } from '@/components/page/RowMenu';
import { SortableTable } from '@/components/page/SortableTable';
import { amountKey } from '@/components/page/sortKey';
import { SwitchField } from '@/components/page/SwitchField';
import { ProblemMessage } from '@/components/ProblemMessage';
import { buttonVariants } from '@/components/ui/button';
import { useCloseGoal, useGoals, useReopenGoal, type GoalList, type GoalSummary } from '@/api/goals';
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

/** Each tab's list, the goals of the same owners folded into one row (#801) unless the switch is off. */
function List({ scope, closed, add }: { scope: 'me' | 'household'; closed: boolean; add: React.ReactNode }) {
  const { t } = useTranslation();
  const search: RootSearch & GoalsSearch = useSearch({ strict: false });
  const navigate = useNavigate();
  const flat = search.flat === 1;
  const answer = useGoals(scope, closed);
  const setFlat = (on: boolean) =>
    void navigate({ to: '/goals', search: (prev: RootSearch & GoalsSearch) => ({ ...prev, flat: on ? (1 as const) : undefined }), replace: true, resetScroll: false });
  // One stable frame whatever the answer: the switch must survive the reload its own change causes (focus).
  return (
    <>
      <SwitchField label={t('lists.group_by_owner')} checked={!flat} onCheckedChange={(on) => setFlat(!on)} />
      <Body answer={answer} closed={closed} add={add} flat={flat} />
    </>
  );
}

type Row = Folded<GoalSummary, GoalList['groups'][number]>;

function Body({ answer, closed, add, flat }: { answer: ReturnType<typeof useGoals>; closed: boolean; add: React.ReactNode; flat: boolean }) {
  const { t } = useTranslation();
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

  const due = (g: Row) => g.parts ? null : g.targetDate ? <LocalDate value={g.targetDate} style="short" /> : <Dash />;
  const monthly = (g: Row) => g.parts ? null : g.monthlyNeeded ? <Money value={g.monthlyNeeded} tabular /> : <Dash />;
  // A group's amounts are the server's totals; a reader who cannot write the goal gets no menu (close and reopen are writes).
  const amounts = (g: Row) => <Amounts goal={g.group ? { ...g, reached: g.group.reached, target: g.group.target } : g} />;
  const actions = (g: Row) => !g.parts && g.canWrite ? <Actions goal={g} /> : null;
  return (
    <SortableTable<Row> key={flat ? 'flat' : 'grouped'} caption={t('goals_list.list')} rows={flat ? rows : foldByOwners(rows, answer.data.groups)}
      rowKey={(g) => (g.parts ? `group:${g.id}` : g.id)}
      subRows={(g) => g.parts} expandLabel={() => t('goals_list.expand_group')}
      rowClassName={(g) => (g.parts ? 'bg-muted/50' : undefined)}
      columns={[
        { id: 'name', header: t('lists.col.name'), sort: (g) => g.name, cell: (g) => <Name goal={g} /> },
        { id: 'owner', header: t('goals_list.col_owners'), cell: (g) => <OwnerChips owners={g.owners} /> },
        { id: 'progress', header: t('goals_list.col_progress'), sort: (g) => g.progressPct ?? Number.NaN, cell: (g) => (g.parts ? null : <GoalProgress pct={g.progressPct} />) },
        { id: 'amounts', header: t('goals_list.col_amounts'), align: 'end', sort: (g) => amountKey((g.group ? g.group.reached : g.reached)?.amount), cell: amounts },
        { id: 'due', header: t('goals_list.col_due'), sort: (g) => g.targetDate ?? '', cell: due },
        { id: 'monthly', header: t('goals_list.col_monthly'), align: 'end', sort: (g) => amountKey(g.monthlyNeeded?.amount), cell: monthly },
        { id: 'status', header: t('goals_list.col_status'), sort: (g) => (g.status ? statusText(t, g.status) : ''), cell: (g) => (g.parts ? null : <GoalStatus status={g.status} />) },
        { id: 'menu', header: t('goals_list.col_actions'), align: 'end', cell: actions },
      ]}
      mobile={(g) => (
        <div className="space-y-1">
          <div className="flex items-center justify-between gap-2"><Name goal={g} /><span className="flex items-center gap-2"><OwnerChips owners={g.owners} />{actions(g)}</span></div>
          {g.parts ? null : <div className="flex flex-wrap items-center justify-between gap-2 text-sm"><GoalProgress pct={g.progressPct} /><GoalStatus status={g.status} /></div>}
          <div className="flex flex-wrap items-center justify-between gap-2 text-sm text-muted-foreground">
            {amounts(g)}
            {g.parts ? null : <span className="inline-flex gap-2"><span>{t('goals_list.col_due')} {due(g)}</span><span>{t('goals_list.col_monthly')} {monthly(g)}</span></span>}
          </div>
        </div>
      )} />
  );
}

/** The name links to the detail; a partial view says so, without saying which envelope is hidden. */
function Name({ goal }: { goal: Row }) {
  const { t } = useTranslation();
  if (goal.parts) return <span className="font-medium">{t('goals_list.group', { count: goal.parts.length })}</span>;
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
      {deleting ? <Suspense fallback={null}><DeleteItemFlow kind="goals.delete_goal" id={goal.id} closed={goal.closed} onClose={() => setDeleting(false)} /></Suspense> : null}
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

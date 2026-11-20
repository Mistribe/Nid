import { keepPreviousData } from '@tanstack/react-query';
import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import { ChevronLeftIcon, ChevronRightIcon } from 'lucide-react';
import { Fragment, lazy, Suspense, useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { useBankAccounts, useBankingStatus, useCategories, type BankCategory } from '@/api/banking';
import { present, toProblem } from '@/api/errors';
import type { SpendingSearch } from '@/app/router';
import { EmptyState } from '@/components/EmptyState';
import { Segmented } from '@/components/form/Segmented';
import { InfoTip } from '@/components/InfoTip';
import { monthText } from '@/components/LocalDate';
import { formatCurrency } from '@/components/Money';
import { PageHeader } from '@/components/page/PageHeader';
import { RowMenu } from '@/components/page/RowMenu';
import { SectionCard } from '@/components/page/SectionCard';
import { Loading, TableSkeleton } from '@/components/page/skeletons';
import { SortableTable } from '@/components/page/SortableTable';
import { StatLine } from '@/components/page/StatLine';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Badge } from '@/components/ui/badge';
import { Button, buttonVariants } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { shiftMonth } from '@/features/calendar/calendarDates';
import { mergeTargets } from './categoryTargets';
import { toPlot } from '@/lib/plot';
import type { RootSearch } from '@/lib/search';
import { todayIso } from '@/lib/today';
import { cn } from '@/lib/utils';
import { SPENDING, type Spending, type SpendingBudget, type SpendingQuery } from './api';
import { BudgetDialog, type BudgetTarget } from './BudgetDialog';
import { CategoryLinesDialog, Incomplete } from './CategoryLinesDialog';
import '@/locales/spending';

/** A server amount that is nought, read as text. */
const ZERO = /^-?0(\.0+)?$/;

// Its own chunk: Recharts loads with the 12-month view only.
const MonthsChart = lazy(() => import('./MonthsChart').then((m) => ({ default: m.MonthsChart })));
// #415: a row's rename or merge, the categories page's own dialogs, loaded when one opens.
const CategoryEdit = lazy(() => import('./categoryEdits').then((m) => ({ default: m.CategoryEdit })));
type Edit = { mode: 'rename' | 'merge'; id: string };

type Row = { id: string; name: string; amount: string; average?: string | null; budget?: SpendingBudget | null; children: Row[]; uncategorised?: true };

// Status colours are the fixed ones (dataviz.md); the badge always says it too.
const PACE: Record<string, string> = {
  on_track: 'bg-status-good/15 text-status-good-text',
  ahead: 'bg-status-warning/15 text-status-warning-text',
  over: 'bg-status-critical/15 text-foreground',
};

/** A row's budget menu: the visible one (table or phone list), to take the focus back after its dialog. */
const menuId = (id: string, mobile = false) => `budget-${id}${mobile ? '-mobile' : ''}`;

/** « Ajouter un budget »: its button's id is `menuId(ADD)`, so the focus goes back to it as to a row's menu. */
const ADD = 'add';

/** An arrow at the end stays focusable (`aria-disabled`), as the year stepper's. */
const END = 'aria-disabled:cursor-default aria-disabled:opacity-50 aria-disabled:hover:bg-transparent';

/**
 * #85: what the scope's bank accounts spent over a month (or the 12 months up to it), by expense category, beside the usual month;
 * a figure opens its lines. Every amount is the server's.
 */
export function SpendingPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const navigate = useNavigate();
  const search: RootSearch & SpendingSearch = useSearch({ strict: false });
  const current = todayIso().slice(0, 7);
  const month = search.month && search.month < current ? search.month : current;
  const year = search.period === '12m';
  const query: SpendingQuery = { scope: search.scope === 'me' ? 'me' : 'household', ...(year ? { from: shiftMonth(month, -11), to: month } : { month }) };
  const status = useBankingStatus();
  const accounts = useBankAccounts();
  const spending = $api.useQuery('get', SPENDING, { params: { query } }, { enabled: (accounts?.length ?? 0) > 0, placeholderData: keepPreviousData });
  const [open, setOpen] = useState<{ categoryId?: string; name: string } | null>(null);
  const [budget, setBudget] = useState<BudgetTarget | 'pick' | null>(null);
  const [edit, setEdit] = useState<Edit | null>(null);
  const categories = useCategories();
  const [focus, setFocus] = useState<string | null>(null);
  useEffect(() => {
    if (focus === null) return;
    [menuId(focus), menuId(focus, true)].map((id) => document.getElementById(id)).find((el) => el && el.checkVisibility?.() !== false)?.focus();
    setFocus(null);
  }, [focus]);
  const closeBudget = () => { setFocus(budget === 'pick' ? ADD : budget?.id ?? null); setBudget(null); };
  const closeEdit = () => { setFocus(edit?.id ?? null); setEdit(null); };

  const set = (patch: SpendingSearch) =>
    void navigate({ from: '/spending', to: '/spending', replace: true, resetScroll: false, search: (prev: RootSearch & SpendingSearch) => ({ ...prev, ...patch }) });
  const go = (by: number) => { const next = shiftMonth(month, by); set({ month: next >= current ? undefined : next }); };

  const controls = (
    <>
      <div role="group" aria-label={t('spending.month')} data-header-control className="inline-flex items-center rounded-full border border-input">
        <Button type="button" variant="ghost" size="icon" aria-label={t('spending.previous')} onClick={() => go(-1)}>
          <ChevronLeftIcon aria-hidden="true" />
        </Button>
        <span aria-live="polite" className="min-w-32 px-1 text-center tabular-nums first-letter:uppercase">{monthText(month)}</span>
        <Button type="button" variant="ghost" size="icon" aria-label={t('spending.next')} aria-disabled={month === current || undefined} className={END}
          onClick={() => { if (month !== current) go(1); }}>
          <ChevronRightIcon aria-hidden="true" />
        </Button>
      </div>
      <div data-header-control>
        <Segmented aria-label={t('spending.period')} value={year ? '12m' : 'month'} onValueChange={(v) => set({ period: v === '12m' ? '12m' : undefined })}
          options={[{ value: 'month', label: t('spending.period_month') }, { value: '12m', label: t('spending.period_12m') }]} />
      </div>
    </>
  );

  const body = () => {
    if (status.data?.enabled === false)
      return <EmptyState module="/spending" title={t('spending.disabled_title')} body={t('spending.disabled_body')} />;
    if (accounts?.length === 0)
      return (
        <EmptyState module="/spending" title={t('spending.no_account_title')} body={t('spending.no_account_body')}
          action={<Link to="/settings/banks" className={buttonVariants({ variant: 'outline' })}>{t('spending.no_account_action')}</Link>} />
      );
    if (spending.error) return <ProblemMessage problem={present(toProblem(spending.error))} />;
    if (!spending.data) return <Loading label={t('spending.loading')}><TableSkeleton columns={4} /></Loading>;
    return <Figures data={spending.data} period={period(spending.data)} year={year} household={query.scope === 'household'} onOpen={setOpen} onBudget={setBudget}
      categories={categories.data ?? []} onEdit={setEdit} />;
  };
  const period = (data: Spending) => (year ? t('spending.range', { from: monthText(data.from), to: monthText(data.to) }) : monthText(data.to));

  return (
    <div className="space-y-4" aria-busy={spending.isPlaceholderData || undefined}>
      <PageHeader title={t('spending.title')} actions={controls} />
      {body()}
      {open && spending.data ? (
        <CategoryLinesDialog target={open} query={query} period={period(spending.data)} onClose={() => setOpen(null)} />
      ) : null}
      {budget ? <BudgetDialog target={budget} onClose={closeBudget} onDone={(message) => { toast.success(message); closeBudget(); }} /> : null}
      {edit && categories.data ? (
        <Suspense fallback={null}>
          <CategoryEdit edit={edit} categories={categories.data} onClose={closeEdit} onDone={(message) => { toast.success(message); closeEdit(); }} />
        </Suspense>
      ) : null}
    </div>
  );
}

function Figures({ data, period, year, household, onOpen, onBudget, categories, onEdit }: {
  data: Spending; period: string; year: boolean; household: boolean; onOpen: (target: { categoryId?: string; name: string }) => void;
  onBudget: (target: BudgetTarget | 'pick') => void; categories: BankCategory[]; onEdit: (edit: Edit) => void;
}) {
  const { t } = useTranslation();
  const money = (amount: string) => formatCurrency(amount, data.currency);
  const uncategorised: Row = { id: 'uncategorised', name: t('spending.uncategorised'), ...data.uncategorised, children: [], uncategorised: true };
  // « Non classées » last, after the server's order (most spent first); shown when it holds something.
  const rows: Row[] = [...data.categories, ...(ZERO.test(data.uncategorised.amount) ? [] : [uncategorised])];
  // The bars only: their widths, never a figure the person reads.
  const widest = Math.max(0, ...rows.map((r) => toPlot(r.amount)));
  const bar = (r: Row) => (
    <span aria-hidden="true" className="block h-2 w-full min-w-16 rounded-full bg-muted">
      <span className="block h-full rounded-full bg-primary" style={{ width: `${widest > 0 ? (Math.max(0, toPlot(r.amount)) / widest) * 100 : 0}%` }} />
    </span>
  );
  const name = (r: Row) => (
    <span className="inline-flex items-center gap-1">
      <button type="button" aria-label={t('spending.open_lines', { name: r.name })} onClick={() => onOpen(r.uncategorised ? { name: r.name } : { categoryId: r.id, name: r.name })}
        className="rounded-sm text-left font-medium underline-offset-4 hover:underline focus-visible:ring-[3px] focus-visible:ring-ring/50 focus-visible:outline-none">
        {r.name}
      </button>
      {r.uncategorised ? <InfoTip label={r.name}>{t('spending.uncategorised_tip')}</InfoTip> : null}
    </span>
  );
  const average = (r: Row) => (r.average ? money(r.average) : '—');
  // The budget is the household's, for one month: Foyer's month view only.
  const budgets = household && !year;
  // What qualifies a budget's amount, each word with its tip: where it comes from, and how much of a typed one is already committed.
  const notes = (r: Row, b: SpendingBudget) => {
    const mark = b.source === 'committed' ? 'committed' : b.period === 'year' ? 'yearly' : null;
    const committed = b.source === 'typed' && b.committed ? t('spending.budget.committed_part', { amount: money(b.committed) }) : null;
    return [
      mark ? <span key="mark" className="inline-flex items-center gap-1">{t(`spending.budget.${mark}`)}<InfoTip label={t('spending.budget.of', { name: r.name })}>{t(`spending.budget.${mark}_tip`)}</InfoTip></span> : null,
      committed ? <span key="committed" className="inline-flex items-center gap-1">{committed}<InfoTip label={committed}>{t('spending.budget.committed_part_tip')}</InfoTip></span> : null,
    ].filter(Boolean);
  };
  const planned = (r: Row) => {
    const b = r.budget;
    if (!b) return '—';
    return (
      <span className="inline-flex flex-wrap items-center justify-end gap-x-1">
        <span className="tabular-nums">{money(b.planned)}</span>
        <span className="contents text-xs text-muted-foreground">{notes(r, b)}</span>
      </span>
    );
  };
  // The server's sign, read off its text: « Dépassé de » the amount without its minus.
  const over = (b: SpendingBudget) => (b.left.startsWith('-') ? t('spending.budget.over_by', { amount: money(b.left.slice(1)) }) : null);
  const left = (r: Row) => (!r.budget ? '—' : over(r.budget) ?? money(r.budget.left));
  // Phone: the budget's facts inline, wrapping as words do (« Budget 1 200,00 € · dont … engagés ⓘ · Reste 40,00 € »).
  const budgetLine = (r: Row) => {
    const b = r.budget;
    if (!b) return <>{t('spending.col_budget')} —</>;
    return (
      <>
        {t('spending.col_budget')} <span className="tabular-nums">{money(b.planned)}</span>
        {notes(r, b).map((note, i) => <Fragment key={i}> · {note}</Fragment>)}
        {' · '}{over(b) ?? <>{t('spending.col_left')} <span className="tabular-nums">{money(b.left)}</span></>}
        {b.state ? <> {pace(r)}</> : null}
      </>
    );
  };
  const pace = (r: Row) => {
    const state = r.budget?.state;
    if (!state) return '—';
    return (
      <span className="inline-flex items-center gap-1">
        <Badge variant="outline" className={cn('border-transparent', PACE[state])}>{t(`spending.budget.state.${state as 'on_track'}`)}</Badge>
        {state === 'ahead' ? (
          <InfoTip label={t('spending.budget.state.ahead')}>
            {r.budget?.expectedSoFar ? t('spending.budget.pace_tip_expected', { amount: money(r.budget.expectedSoFar) }) : t('spending.budget.pace_tip')}
          </InfoTip>
        ) : null}
      </span>
    );
  };
  // A child under a parent's typed budget has none of its own to set (one budget per branch).
  const underTyped = new Set(data.categories.filter((c) => c.budget?.source === 'typed').flatMap((c) => c.children.map((child) => child.id)));
  // #415: a category is renamed or merged where it is noticed; merging needs another of its kind to go into.
  const menu = (r: Row, mobile = false) => {
    if (r.uncategorised) return null;
    const items = [
      ...(budgets && !underTyped.has(r.id) ? [{ label: t('spending.budget.edit'), onSelect: () => onBudget({ id: r.id, name: r.name, budget: r.budget }) }] : []),
      ...(categories.some((c) => c.id === r.id) ? [{ label: t('spending.rename'), onSelect: () => onEdit({ mode: 'rename', id: r.id }) }] : []),
      ...(categories.some((c) => c.id === r.id && mergeTargets(categories, c).length > 0) ? [{ label: t('spending.merge'), onSelect: () => onEdit({ mode: 'merge', id: r.id }) }] : []),
    ];
    return items.length === 0 ? null : <RowMenu id={menuId(r.id, mobile)} label={t('spending.budget.actions', { name: r.name })} items={items} />;
  };

  return (
    <>
      <StatLine items={[
        { label: t('spending.total'), value: money(data.total), hint: period },
        { label: t('spending.average'), value: data.average ? money(data.average) : '—', hint: data.average ? t(year ? 'spending.average_hint_period' : 'spending.average_hint') : t('spending.average_none'), tip: t('spending.average_tip') },
        { label: t('spending.uncategorised'), value: money(data.uncategorised.amount), tip: t('spending.uncategorised_tip') },
      ]} />
      {data.incomplete ? <Incomplete /> : null}
      {budgets && data.budgetIncomplete ? <Incomplete text={t('spending.budget_incomplete')} /> : null}
      {year ? (
        <Suspense fallback={<Loading label={t('spending.loading')}><Skeleton className="h-48 w-full" /></Loading>}>
          <MonthsChart months={data.months} currency={data.currency} />
        </Suspense>
      ) : null}
      <SectionCard title={t('spending.by_category')} note={period} actions={budgets ? (
        <Button id={menuId(ADD)} type="button" variant="outline" size="sm" onClick={() => onBudget('pick')}>{t('spending.budget.add')}</Button>
      ) : null}>
        {data.categories.length === 0 ? (
          <>
            <EmptyState module="/spending" level={3} title={t('spending.no_category_title')} body={t('spending.no_category_body')}
              action={<Link to="/investments" className={buttonVariants({ variant: 'outline' })}>{t('spending.no_category_action')}</Link>} />
            {/* Foyer leaves out the accounts only the person reads: they may hold every line. */}
            {household ? (
              <p className="pb-6 text-center text-sm text-muted-foreground">
                {t('spending.no_category_mine')}{' '}
                <Link from="/spending" to="/spending" search={(prev: RootSearch & SpendingSearch) => ({ ...prev, scope: 'me' as const })}
                  className="font-medium text-foreground underline underline-offset-4">{t('spending.no_category_mine_action')}</Link>
              </p>
            ) : null}
          </>
        ) : null}
        {rows.length > 0 ? (
          <SortableTable<Row> caption={t('spending.by_category')} rows={rows} rowKey={(r) => r.id}
            subRows={(r) => r.children} expandSingle expandLabel={(r) => t('spending.expand', { name: r.name })}
            columns={[
              // Not `wrap`: the chevron and the name stay on one line.
              { id: 'name', header: t('spending.col_category'), cell: name },
              { id: 'amount', header: t('spending.col_amount'), align: 'end', cell: (r) => money(r.amount) },
              // A period's amounts cover its months, the usual month one: side by side they would mislead.
              ...(year ? [] : [{ id: 'average', header: t('spending.col_average'), align: 'end' as const, cell: average }]),
              ...(budgets ? [
                { id: 'budget', header: t('spending.col_budget'), align: 'end' as const, cell: planned },
                { id: 'left', header: t('spending.col_left'), align: 'end' as const, cell: left },
                { id: 'pace', header: t('spending.col_pace'), cell: pace },
              ] : []),
              { id: 'share', header: t('spending.col_share'), className: budgets ? 'w-1/6' : 'w-1/4', cell: bar },
              { id: 'actions', header: t('spending.col_actions'), align: 'end' as const, cell: (r: Row) => menu(r) },
            ]}
            mobile={(r) => (
              <div className="space-y-1">
                <div className="flex items-start justify-between gap-2">{name(r)}<span className="tabular-nums">{money(r.amount)}</span>{budgets ? null : menu(r, true)}</div>
                {bar(r)}
                {year ? null : <div className="text-sm text-muted-foreground">{t('spending.average_line', { amount: average(r) })}</div>}
                {budgets && !r.uncategorised ? (
                  <div className="flex items-start justify-between gap-2 text-sm">
                    <p className="text-muted-foreground">{budgetLine(r)}</p>
                    {menu(r, true)}
                  </div>
                ) : null}
              </div>
            )} />
        ) : null}
        {/* Moi: the budget is the household's, never shown against one person's accounts. */}
        {!household && !year ? (
          <p className="pt-3 text-sm text-muted-foreground">
            {t('spending.budget.household_only')}{' '}
            <Link from="/spending" to="/spending" search={(prev: RootSearch & SpendingSearch) => ({ ...prev, scope: undefined })}
              className="font-medium text-foreground underline underline-offset-4">{t('spending.budget.household_only_action')}</Link>
          </p>
        ) : null}
      </SectionCard>
    </>
  );
}

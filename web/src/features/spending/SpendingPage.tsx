import { keepPreviousData } from '@tanstack/react-query';
import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import { ChevronLeftIcon, ChevronRightIcon } from 'lucide-react';
import { lazy, Suspense, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { useBankAccounts, useBankingStatus } from '@/api/banking';
import { present, toProblem } from '@/api/errors';
import type { SpendingSearch } from '@/app/router';
import { EmptyState } from '@/components/EmptyState';
import { Segmented } from '@/components/form/Segmented';
import { InfoTip } from '@/components/InfoTip';
import { monthText } from '@/components/LocalDate';
import { formatCurrency } from '@/components/Money';
import { PageHeader } from '@/components/page/PageHeader';
import { SectionCard } from '@/components/page/SectionCard';
import { Loading, TableSkeleton } from '@/components/page/skeletons';
import { SortableTable } from '@/components/page/SortableTable';
import { StatLine } from '@/components/page/StatLine';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button, buttonVariants } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { shiftMonth } from '@/features/calendar/calendarDates';
import { toPlot } from '@/lib/plot';
import type { RootSearch } from '@/lib/search';
import { todayIso } from '@/lib/today';
import { SPENDING, type Spending, type SpendingQuery } from './api';
import { CategoryLinesDialog, Incomplete } from './CategoryLinesDialog';
import '@/locales/spending';

/** A server amount that is nought, read as text. */
const ZERO = /^-?0(\.0+)?$/;

// Its own chunk: Recharts loads with the 12-month view only.
const MonthsChart = lazy(() => import('./MonthsChart').then((m) => ({ default: m.MonthsChart })));

type Row = { id: string; name: string; amount: string; average?: string | null; children: Row[]; uncategorised?: true };

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
    return <Figures data={spending.data} period={period(spending.data)} year={year} household={query.scope === 'household'} onOpen={setOpen} />;
  };
  const period = (data: Spending) => (year ? t('spending.range', { from: monthText(data.from), to: monthText(data.to) }) : monthText(data.to));

  return (
    <div className="space-y-4" aria-busy={spending.isPlaceholderData || undefined}>
      <PageHeader title={t('spending.title')} actions={controls} />
      {body()}
      {open && spending.data ? (
        <CategoryLinesDialog target={open} query={query} currency={spending.data.currency} period={period(spending.data)} onClose={() => setOpen(null)} />
      ) : null}
    </div>
  );
}

function Figures({ data, period, year, household, onOpen }: {
  data: Spending; period: string; year: boolean; household: boolean; onOpen: (target: { categoryId?: string; name: string }) => void;
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

  return (
    <>
      <StatLine items={[
        { label: t('spending.total'), value: money(data.total), hint: period },
        { label: t('spending.average'), value: data.average ? money(data.average) : '—', hint: data.average ? t(year ? 'spending.average_hint_period' : 'spending.average_hint') : t('spending.average_none'), tip: t('spending.average_tip') },
        { label: t('spending.uncategorised'), value: money(data.uncategorised.amount), tip: t('spending.uncategorised_tip') },
      ]} />
      {data.incomplete ? <Incomplete /> : null}
      {year ? (
        <Suspense fallback={<Loading label={t('spending.loading')}><Skeleton className="h-48 w-full" /></Loading>}>
          <MonthsChart months={data.months} currency={data.currency} />
        </Suspense>
      ) : null}
      <SectionCard title={t('spending.by_category')} note={period}>
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
              { id: 'name', header: t('spending.col_category'), wrap: true, cell: name },
              { id: 'amount', header: t('spending.col_amount'), align: 'end', cell: (r) => money(r.amount) },
              // A period's amounts cover its months, the usual month one: side by side they would mislead.
              ...(year ? [] : [{ id: 'average', header: t('spending.col_average'), align: 'end' as const, cell: average }]),
              { id: 'share', header: t('spending.col_share'), className: 'w-1/4', cell: bar },
            ]}
            mobile={(r) => (
              <div className="space-y-1">
                <div className="flex items-start justify-between gap-2">{name(r)}<span className="tabular-nums">{money(r.amount)}</span></div>
                {bar(r)}
                {year ? null : <div className="text-sm text-muted-foreground">{t('spending.average_line', { amount: average(r) })}</div>}
              </div>
            )} />
        ) : null}
      </SectionCard>
    </>
  );
}

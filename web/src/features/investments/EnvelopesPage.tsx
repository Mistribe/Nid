import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import { Archive, Clock } from 'lucide-react';
import { lazy, Suspense, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import type { InvestmentsSearch } from '@/app/router';
import type { RootSearch } from '@/lib/search';
import { EmptyState } from '@/components/EmptyState';
import { LocalDate } from '@/components/LocalDate';
import { Money } from '@/components/Money';
import { OwnerChips } from '@/components/OwnerChips';
import { DetailTabs, useDetailTab } from '@/components/page/DetailTabs';
import { PageHeader } from '@/components/page/PageHeader';
import { SortableTable } from '@/components/page/SortableTable';
import { amountKey } from '@/components/page/sortKey';
import { SwitchField } from '@/components/page/SwitchField';
import { ProblemMessage } from '@/components/ProblemMessage';
import { buttonVariants } from '@/components/ui/button';
import { ENVELOPES, HOUSEHOLD_HOLDINGS, type EnvelopeList, type EnvelopeSummary } from '@/api/investments';
import { kindText } from './format';
import { Gain } from './Figures';
import '@/locales/investments';
import '@/locales/forms';
import { Loading, OverviewSkeleton, TableSkeleton } from '@/components/page/skeletons';

function useEnvelopes(scope: 'me' | 'household', closed: boolean) {
  const { $api } = useApi();
  return $api.useQuery('get', ENVELOPES, { params: { query: { scope, ...(closed ? { archived: true } : {}) } } });
}

const OverviewTab = lazy(() => import('./overview/OverviewTab').then((m) => ({ default: m.OverviewTab })));

const AnalysisTab = lazy(() => import('./overview/OverviewTab').then((m) => ({ default: m.AnalysisTab })));

const PerformanceTab = lazy(() => import('./overview/PerformanceTab').then((m) => ({ default: m.PerformanceTab })));

const IncomeTab = lazy(() => import('./overview/IncomeTab'));

const HoldingsTab = lazy(() => import('./overview/HoldingsTab').then((m) => ({ default: m.HoldingsTab })));

const WITH_HOLDINGS = ['overview', 'analysis', 'performance', 'income', 'holdings', 'list'] as const;
const WITHOUT_HOLDINGS = ['overview', 'analysis', 'performance', 'income', 'list'] as const;

/**
 * Investments dashboard §3.1, §4.3 and §5.3: the overview opens first (the simple view, issue #20), then « Analyse » (the rest of the
 * overview), « Performance », « Supports et frais » while an envelope has a snapshot; the list keeps its switch of the closed envelopes
 * under `?tab=list`.
 */
export function EnvelopesPage() {
  const { t } = useTranslation();
  const search: RootSearch & InvestmentsSearch = useSearch({ strict: false });
  const scope = search.scope === 'me' ? 'me' : 'household';
  const { $api } = useApi();
  // §5.1 D20: « Supports et frais » only while an envelope has a snapshot; its answer is shared with the tab.
  const detailed = ($api.useQuery('get', HOUSEHOLD_HOLDINGS, { params: { query: { scope } } }).data?.detailedCount ?? 0) > 0;
  const [tab, setTab] = useDetailTab<(typeof WITH_HOLDINGS)[number]>(detailed ? WITH_HOLDINGS : WITHOUT_HOLDINGS);
  // The empty state's action repeats the header's primary: `outline`, one primary per view (design-system.md §6.1).
  const add = (variant?: 'outline') => <Link to="/investments/new" className={buttonVariants({ variant })}>{t('investments.add')}</Link>;
  return (
    <div className="space-y-4">
      <PageHeader title={t('investments.title')} actions={add()} />
      <DetailTabs value={tab} onChange={setTab} tabs={[
        { id: 'overview', label: t('investments_overview.tab_overview'),
          content: <Suspense fallback={<OverviewSkeleton label={t('page.loading')} />}><OverviewTab scope={scope} span={search.curve ?? '12m'} /></Suspense> },
        { id: 'analysis', label: t('investments_overview.tab_analysis'),
          content: <Suspense fallback={<OverviewSkeleton label={t('page.loading')} />}><AnalysisTab scope={scope} span={search.curve ?? '12m'} /></Suspense> },
        { id: 'performance', label: t('investments_overview.tab_performance'),
          content: <Suspense fallback={<OverviewSkeleton label={t('page.loading')} />}><PerformanceTab scope={scope} /></Suspense> },
        { id: 'income', label: t('investments_overview.tab_income'),
          content: <Suspense fallback={<OverviewSkeleton label={t('page.loading')} />}><IncomeTab scope={scope} /></Suspense> },
        ...(detailed ? [{ id: 'holdings' as const, label: t('investments_overview.tab_holdings'),
          content: <Suspense fallback={<OverviewSkeleton label={t('page.loading')} />}><HoldingsTab scope={scope} /></Suspense> }] : []),
        { id: 'list', label: t('investments_overview.tab_list'), content: <ListTab scope={scope} add={add('outline')} /> },
      ]} />
    </div>
  );
}

/** The list of envelopes with their value, gain and staleness (2B2a design). */
function ListTab({ scope, add }: { scope: 'me' | 'household'; add: ReactNode }) {
  const { t } = useTranslation();
  const search: RootSearch & InvestmentsSearch = useSearch({ strict: false });
  const navigate = useNavigate();
  const closed = search.closed === 1;
  const answer = useEnvelopes(scope, closed);
  const flat = search.flat === 1;
  const set = (key: 'closed' | 'flat', on: boolean) =>
    void navigate({ to: '/investments', search: (prev: RootSearch & InvestmentsSearch) => ({ ...prev, [key]: on ? (1 as const) : undefined }), replace: true, resetScroll: false });

  // One stable frame whatever the answer: the switch must survive the reload its own change causes (focus).
  return (
    <>
      <div className="flex flex-wrap gap-x-6 gap-y-2">
        <SwitchField label={t('investments.show_closed')} checked={closed} onCheckedChange={(on) => set('closed', on)} />
        <SwitchField label={t('lists.group_by_owner')} checked={!flat} onCheckedChange={(on) => set('flat', !on)} />
      </div>
      <Body answer={answer} add={add} flat={flat} />
    </>
  );
}

function Body({ answer, add, flat }: { answer: ReturnType<typeof useEnvelopes>; add: ReactNode; flat: boolean }) {
  const { t } = useTranslation();
  if (answer.error) return <ProblemMessage problem={present(toProblem(answer.error))} />;

  if (!answer.data)
    return (
      <Loading label={t('investments.loading')}><TableSkeleton columns={6} /></Loading>
    );

  const data = answer.data;
  if (data.items.length === 0)
    return <EmptyState module="/investments" title={t('investments.empty_title')} body={t('investments.empty_body')} action={add} />;

  // As the income list: the envelopes of the same owners fold into one row, its total the server's.
  const value = (r: Row) => (r.parts ? r.total : r.figures.value);
  const valueCell = (r: Row) => value(r) ? <Money value={value(r)!} tabular /> : <span className="text-muted-foreground">{r.parts ? t('investments.dash') : t('investments.no_value')}</span>;
  return (
    <>
      <p className="text-sm text-muted-foreground">{t('investments.before_tax')}</p>
      <SortableTable<Row> caption={t('investments.list')} rows={flat ? data.items : rows(data)} rowKey={(r) => (r.parts ? `group:${r.id}` : r.id)}
        rowLink={(r) => ({ to: '/investments/$id', params: { id: r.id } })}
        subRows={(r) => r.parts} expandLabel={() => t('investments.expand_group')}
        rowClassName={(r) => (r.parts ? 'bg-muted/50' : undefined)}
        columns={[
          { id: 'name', header: t('lists.col.name'), sort: (r) => r.displayName, cell: (r) => <Name item={r} /> },
          { id: 'kind', header: t('lists.col.kind'), sort: (r) => kindText(t, r.kind), cell: (r) => (r.parts ? null : kindText(t, r.kind)) },
          { id: 'owner', header: t('lists.col.owner'), cell: (r) => <OwnerChips owners={r.owners} /> },
          { id: 'value', header: t('lists.col.value'), align: 'end', sort: (r) => amountKey(value(r)?.amount), cell: valueCell },
          { id: 'gain', header: t('lists.col.gain'), align: 'end', cell: (r) => r.parts ? null : r.figures.value ? <Gain figures={r.figures} /> : <span aria-hidden="true">{t('investments.dash')}</span> },
          { id: 'updated', header: t('lists.col.updated'), sort: (r) => r.figures.asOf ?? '', cell: (r) => (r.parts ? null : <Updated item={r} />) },
        ]}
        mobile={(r) => (
          <div className="space-y-1">
            <div className="flex items-center justify-between gap-2"><Name item={r} /><OwnerChips owners={r.owners} /></div>
            <div className="flex flex-wrap items-center justify-between gap-2 text-sm">
              {valueCell(r)}
              {!r.parts && r.figures.value ? <Gain figures={r.figures} /> : null}
            </div>
          </div>
        )} />
    </>
  );
}

type Row = EnvelopeSummary & { parts?: EnvelopeSummary[]; total?: EnvelopeList['groups'][number]['value'] };

/** A group of one stays a plain row; a gathered row takes its leading envelope's id, for its key. */
function rows(data: EnvelopeList): Row[] {
  const byId = new Map(data.items.map((i) => [i.id, i]));
  return data.groups.flatMap((g) => {
    const parts = g.items.flatMap((id) => byId.get(id) ?? []);
    return parts.length > 1 ? [{ ...parts[0]!, parts, total: g.value }] : parts;
  });
}

function Name({ item }: { item: Row }) {
  const { t } = useTranslation();
  if (item.parts) return <span className="font-medium">{t('investments.group', { count: item.parts.length })}</span>;
  return (
    <span className="inline-flex flex-wrap items-center gap-2">
      <span className="font-medium">{item.displayName}</span>
      {item.closed ? <span className="inline-flex items-center gap-1 text-sm text-muted-foreground"><Archive aria-hidden="true" className="size-4" />{t('investments.closed_mark')}</span> : null}
    </span>
  );
}

/** The value's date; an old one carries a clock and says its age in words. */
function Updated({ item }: { item: EnvelopeSummary }) {
  const { t } = useTranslation();
  const { asOf, staleDays } = item.figures;
  if (!asOf) return <span aria-hidden="true">{t('investments.dash')}</span>;
  const stale = staleDays !== null && staleDays !== undefined;
  return (
    <span className="inline-flex items-center gap-1">
      {stale ? <Clock aria-hidden="true" className="size-4" /> : null}
      <LocalDate value={asOf} style="short" />
      {stale ? <span className="sr-only">{t('investments.stale', { count: staleDays })}</span> : null}
    </span>
  );
}

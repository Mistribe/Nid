import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import { Archive, Clock } from 'lucide-react';
import { lazy, Suspense, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import type { InvestmentsSearch, RootSearch } from '@/app/router';
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
import { Skeleton } from '@/components/ui/skeleton';
import { ENVELOPES, HOUSEHOLD_HOLDINGS, type EnvelopeSummary } from './api';
import { kindText } from './format';
import { Gain } from './Figures';
import '@/locales/investments';

function useEnvelopes(scope: 'me' | 'household', closed: boolean) {
  const { $api } = useApi();
  return $api.useQuery('get', ENVELOPES, { params: { query: { scope, ...(closed ? { archived: true } : {}) } } });
}

const OverviewTab = lazy(() => import('./overview/OverviewTab').then((m) => ({ default: m.OverviewTab })));

const PerformanceTab = lazy(() => import('./overview/PerformanceTab').then((m) => ({ default: m.PerformanceTab })));

const HoldingsTab = lazy(() => import('./overview/HoldingsTab').then((m) => ({ default: m.HoldingsTab })));

const WITH_HOLDINGS = ['overview', 'performance', 'holdings', 'list'] as const;
const WITHOUT_HOLDINGS = ['overview', 'performance', 'list'] as const;

/**
 * Investments dashboard §3.1, §4.3 and §5.3: the overview opens first, then « Performance », « Supports et frais » while an envelope has a
 * snapshot; the list keeps its switch of the closed envelopes under `?tab=list`.
 */
export function EnvelopesPage() {
  const { t } = useTranslation();
  const search: RootSearch & InvestmentsSearch = useSearch({ strict: false });
  const scope = search.scope === 'me' ? 'me' : 'household';
  const { $api } = useApi();
  // §5.1 D20: « Supports et frais » only while an envelope has a snapshot; its answer is shared with the tab.
  const detailed = ($api.useQuery('get', HOUSEHOLD_HOLDINGS, { params: { query: { scope } } }).data?.detailedCount ?? 0) > 0;
  const [tab, setTab] = useDetailTab<(typeof WITH_HOLDINGS)[number]>(detailed ? WITH_HOLDINGS : WITHOUT_HOLDINGS);
  const add = <Link to="/investments/new" className={buttonVariants()}>{t('investments.add')}</Link>;
  return (
    <div className="space-y-4">
      <PageHeader title={t('investments.title')} actions={add} />
      <DetailTabs value={tab} onChange={setTab} tabs={[
        { id: 'overview', label: t('investments_overview.tab_overview'),
          content: <Suspense fallback={<Skeleton className="h-64 w-full" />}><OverviewTab scope={scope} span={search.curve ?? '12m'} /></Suspense> },
        { id: 'performance', label: t('investments_overview.tab_performance'),
          content: <Suspense fallback={<Skeleton className="h-64 w-full" />}><PerformanceTab scope={scope} /></Suspense> },
        ...(detailed ? [{ id: 'holdings' as const, label: t('investments_overview.tab_holdings'),
          content: <Suspense fallback={<Skeleton className="h-64 w-full" />}><HoldingsTab scope={scope} /></Suspense> }] : []),
        { id: 'list', label: t('investments_overview.tab_list'), content: <ListTab scope={scope} add={add} /> },
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
  const setClosed = (checked: boolean) =>
    void navigate({ to: '/investments', search: (prev: RootSearch & InvestmentsSearch) => ({ ...prev, closed: checked ? (1 as const) : undefined }), replace: true });

  // One stable frame whatever the answer: the switch must survive the reload its own change causes (focus).
  return (
    <>
      <SwitchField label={t('investments.show_closed')} checked={closed} onCheckedChange={setClosed} />
      <Body answer={answer} add={add} />
    </>
  );
}

function Body({ answer, add }: { answer: ReturnType<typeof useEnvelopes>; add: ReactNode }) {
  const { t } = useTranslation();
  if (answer.error) return <ProblemMessage problem={present(toProblem(answer.error))} />;

  if (!answer.data)
    return (
      <ul aria-busy="true" aria-label={t('investments.loading')} className="space-y-2">
        {[0, 1, 2].map((i) => <li key={i} className="h-16 shimmer rounded-md border bg-muted" />)}
      </ul>
    );

  const data = answer.data;
  if (data.items.length === 0)
    return <EmptyState title={t('investments.empty_title')} body={t('investments.empty_body')} action={add} />;

  return (
    <>
      <p className="text-sm text-muted-foreground">{t('investments.before_tax')}</p>
      <SortableTable<EnvelopeSummary> caption={t('investments.list')} rows={data.items} rowKey={(r) => r.id}
        rowLink={(r) => ({ to: '/investments/$id', params: { id: r.id } })}
        columns={[
          { id: 'name', header: t('lists.col.name'), sort: (r) => r.displayName, cell: (r) => <Name item={r} /> },
          { id: 'kind', header: t('lists.col.kind'), sort: (r) => kindText(t, r.kind), cell: (r) => kindText(t, r.kind) },
          { id: 'owner', header: t('lists.col.owner'), cell: (r) => <OwnerChips owners={r.owners} /> },
          { id: 'value', header: t('lists.col.value'), align: 'end', sort: (r) => amountKey(r.figures.value?.amount),
            cell: (r) => r.figures.value ? <Money value={r.figures.value} tabular /> : <span className="text-muted-foreground">{t('investments.no_value')}</span> },
          { id: 'gain', header: t('lists.col.gain'), align: 'end', cell: (r) => r.figures.value ? <Gain figures={r.figures} /> : <span aria-hidden="true">{t('investments.dash')}</span> },
          { id: 'updated', header: t('lists.col.updated'), sort: (r) => r.figures.asOf ?? '', cell: (r) => <Updated item={r} /> },
        ]}
        mobile={(r) => (
          <div className="space-y-1">
            <div className="flex items-center justify-between gap-2"><Name item={r} /><OwnerChips owners={r.owners} /></div>
            <div className="flex flex-wrap items-center justify-between gap-2 text-sm">
              {r.figures.value ? <Money value={r.figures.value} tabular /> : <span className="text-muted-foreground">{t('investments.no_value')}</span>}
              {r.figures.value ? <Gain figures={r.figures} /> : null}
            </div>
          </div>
        )} />
    </>
  );
}

function Name({ item }: { item: EnvelopeSummary }) {
  const { t } = useTranslation();
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

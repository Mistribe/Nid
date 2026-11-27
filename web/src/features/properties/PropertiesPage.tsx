import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import { Clock } from 'lucide-react';
import { lazy, Suspense, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import type { PropertiesSearch } from '@/app/router';
import type { RootSearch } from '@/lib/search';
import { AccessBadge } from '@/components/AccessBadge';
import { EmptyState } from '@/components/EmptyState';
import { SwitchField } from '@/components/page/SwitchField';
import { LocalDate } from '@/components/LocalDate';
import { OwnerChips } from '@/components/OwnerChips';
import { DetailTabs, useDetailTab } from '@/components/page/DetailTabs';
import { foldByOwners, type Folded } from '@/components/page/foldByOwners';
import { PageHeader } from '@/components/page/PageHeader';
import { SortableTable } from '@/components/page/SortableTable';
import { amountKey } from '@/components/page/sortKey';
import { ProblemMessage } from '@/components/ProblemMessage';
import { buttonVariants } from '@/components/ui/button';
import { money, percent } from '../loans/format';
import { PROPERTIES, PROPERTIES_RENTAL, type PropertyList, type PropertySummary } from '@/api/properties';
import { typeText } from './format';
import '@/locales/properties';
import '@/locales/forms';
import { Loading, OverviewSkeleton, TableSkeleton } from '@/components/page/skeletons';

const OverviewTab = lazy(() => import('./overview/OverviewTab').then((m) => ({ default: m.OverviewTab })));

const AnalysisTab = lazy(() => import('./overview/OverviewTab').then((m) => ({ default: m.AnalysisTab })));

const RentalTab = lazy(() => import('./overview/RentalTab').then((m) => ({ default: m.RentalTab })));

const CostsTab = lazy(() => import('./overview/CostsTab').then((m) => ({ default: m.CostsTab })));

const ValuationTab = lazy(() => import('./overview/ValuationTab').then((m) => ({ default: m.ValuationTab })));

const WITH_RENTAL = ['overview', 'analysis', 'rental', 'costs', 'valuation', 'list'] as const;
const WITHOUT_RENTAL = ['overview', 'analysis', 'costs', 'valuation', 'list'] as const;

/**
 * Properties dashboard §3.1, §4.3, §5.3 and §6.3: the overview opens first (the simple view, issue #20), then « Analyse » (the rest
 * of the overview), « Location » while a property is let, « Charges et coûts », « Valorisation »; the list under `?tab=list`.
 */
export function PropertiesPage() {
  const { t } = useTranslation();
  const search: RootSearch & PropertiesSearch = useSearch({ strict: false });
  const scope = search.scope === 'me' ? 'me' : 'household';
  const { $api } = useApi();
  // §4.1 D10: « Location » only while a property is let; its answer is shared with the tab.
  const rented = ($api.useQuery('get', PROPERTIES_RENTAL, { params: { query: { scope } } }).data?.count ?? 0) > 0;
  const [tab, setTab] = useDetailTab<(typeof WITH_RENTAL)[number]>(rented ? WITH_RENTAL : WITHOUT_RENTAL);
  // The empty state's action repeats the header's primary: `outline`, one primary per view (design-system.md §6.1).
  const add = (variant?: 'outline') => <Link to="/properties/new" className={buttonVariants({ variant })}>{t('properties.add')}</Link>;
  return (
    <div className="space-y-4">
      <PageHeader title={t('properties.title')} actions={add()} />
      <DetailTabs value={tab} onChange={setTab} tabs={[
        { id: 'overview', label: t('properties_overview.tab_overview'),
          content: <Suspense fallback={<OverviewSkeleton label={t('page.loading')} />}><OverviewTab scope={scope} span={search.history ?? '3y'} /></Suspense> },
        { id: 'analysis', label: t('properties_overview.tab_analysis'),
          content: <Suspense fallback={<OverviewSkeleton label={t('page.loading')} />}><AnalysisTab scope={scope} span={search.history ?? '3y'} /></Suspense> },
        ...(rented ? [{ id: 'rental' as const, label: t('properties_rental.tab'),
          content: <Suspense fallback={<OverviewSkeleton label={t('page.loading')} />}><RentalTab scope={scope} property={search.property} /></Suspense> }] : []),
        { id: 'costs', label: t('properties_costs.tab'),
          content: <Suspense fallback={<OverviewSkeleton label={t('page.loading')} />}><CostsTab scope={scope} property={search.property} /></Suspense> },
        { id: 'valuation', label: t('properties_valuation.tab'),
          content: <Suspense fallback={<OverviewSkeleton label={t('page.loading')} />}><ValuationTab scope={scope} /></Suspense> },
        { id: 'list', label: t('properties_overview.tab_list'), content: <ListTab scope={scope} add={add('outline')} /> },
      ]} />
    </div>
  );
}

/** The properties with their value today and their net value (3D1 design §4), those of the same owners folded into one row (#799). */
function ListTab({ scope, add }: { scope: 'me' | 'household'; add: ReactNode }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const search: RootSearch & PropertiesSearch = useSearch({ strict: false });
  const navigate = useNavigate();
  const flat = search.flat === 1;
  const setFlat = (on: boolean) =>
    void navigate({ to: '/properties', search: (prev: RootSearch & PropertiesSearch) => ({ ...prev, flat: on ? (1 as const) : undefined }), replace: true, resetScroll: false });
  const answer = $api.useQuery('get', PROPERTIES, { params: { query: { scope } } });

  // One stable frame whatever the answer: the switch must survive the reload its own change causes (focus).
  return (
    <>
      <SwitchField label={t('lists.group_by_owner')} checked={!flat} onCheckedChange={(on) => setFlat(!on)} />
      <Body answer={answer} add={add} flat={flat} />
    </>
  );
}

type Row = Folded<PropertySummary, PropertyList['groups'][number]>;

function Body({ answer, add, flat }: { answer: { data?: PropertyList; error?: unknown }; add: ReactNode; flat: boolean }) {
  const { t } = useTranslation();
  if (answer.error) return <ProblemMessage problem={present(toProblem(answer.error))} />;
  if (!answer.data)
    return (
      <Loading label={t('properties.loading')}><TableSkeleton columns={6} /></Loading>
    );
  const { items, groups } = answer.data;
  if (items.length === 0) return <EmptyState module="/properties" title={t('properties.empty_title')} body={t('properties.empty_body')} action={add} />;
  const name = (r: Row) => r.parts
    ? <span className="font-medium">{t('properties.group', { count: r.parts.length })}</span>
    : <span className="inline-flex flex-wrap items-center gap-2"><span className="font-medium">{r.name}</span><AccessBadge access={r.visibility === 'private' ? 'private' : 'shared'} /></span>;
  const total = (m: { amount: string; currency: string } | null | undefined) => (m ? <span className="tabular-nums">{money(m.currency, m.amount)}</span> : <Dash />);
  const value = (r: Row) => (r.group ? total(r.group.value) : <Value item={r} />);
  const net = (r: Row) => (r.group ? total(r.group.netValue) : r.netValue ? money(r.currency, r.netValue) : <Dash />);
  return (
    <SortableTable<Row> key={flat ? 'flat' : 'grouped'} caption={t('properties.list')} rows={flat ? items : foldByOwners(items, groups)} rowKey={(r) => (r.parts ? `group:${r.id}` : r.id)}
      rowLink={(r) => ({ to: '/properties/$id', params: { id: r.id } })}
      subRows={(r) => r.parts} expandLabel={() => t('properties.expand_group')}
      rowClassName={(r) => (r.parts ? 'bg-muted/50' : undefined)}
      columns={[
        { id: 'name', header: t('lists.col.name'), sort: (r) => r.name, cell: name },
        { id: 'type', header: t('lists.col.kind'), sort: (r) => typeText(t, r.type), cell: (r) => (r.parts ? null : typeText(t, r.type)) },
        { id: 'owner', header: t('lists.col.owner'), cell: (r) => <OwnerChips owners={r.owners} /> },
        { id: 'value', header: t('lists.col.value'), align: 'end', sort: (r) => amountKey(r.group ? r.group.value?.amount : r.value), cell: value },
        { id: 'net', header: t('lists.col.net_value'), align: 'end', sort: (r) => amountKey(r.group ? r.group.netValue?.amount : r.netValue), cell: net },
        // Spec 3D2 §5: the server's fractions in %, « — » when it gives none.
        { id: 'yield', header: t('lists.col.gross_yield'), align: 'end', sort: (r) => amountKey(r.grossYield), cell: (r) => r.parts ? null : r.grossYield ? percent(t, r.grossYield) : <Dash /> },
      ]}
      mobile={(r) => (
        <div className="space-y-1">
          <div className="flex items-center justify-between gap-2">{name(r)}<OwnerChips owners={r.owners} /></div>
          <div className="flex flex-wrap items-center justify-between gap-2 text-sm">
            {value(r)}
            <span className="text-muted-foreground">{net(r)}</span>
          </div>
        </div>
      )} />
  );
}

function Dash() {
  const { t } = useTranslation();
  return <span aria-hidden="true">{t('properties.unknown_rate')}</span>;
}

/** The estimate with its date; a stale one carries a clock and says « À réestimer » in words. */
function Value({ item }: { item: PropertySummary }) {
  const { t } = useTranslation();
  if (!item.value || !item.valueOn) return <span className="text-muted-foreground">{t('properties.not_estimated')}</span>;
  return (
    <span className="inline-flex flex-col items-end">
      <span className="tabular-nums">{money(item.currency, item.value)}</span>
      <span className="inline-flex items-center gap-1 text-xs text-muted-foreground">
        {item.stale ? <><Clock aria-hidden="true" className="size-3.5" />{t('properties.stale')}<span aria-hidden="true">{t('lists.sep')}</span></> : null}
        <LocalDate value={item.valueOn} />
      </span>
    </span>
  );
}

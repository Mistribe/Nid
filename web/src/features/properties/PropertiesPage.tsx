import { Link, useSearch } from '@tanstack/react-router';
import { Clock } from 'lucide-react';
import { lazy, Suspense, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import type { PropertiesSearch } from '@/app/router';
import type { RootSearch } from '@/lib/search';
import { AccessBadge } from '@/components/AccessBadge';
import { EmptyState } from '@/components/EmptyState';
import { LocalDate } from '@/components/LocalDate';
import { OwnerChips } from '@/components/OwnerChips';
import { DetailTabs, useDetailTab } from '@/components/page/DetailTabs';
import { PageHeader } from '@/components/page/PageHeader';
import { SortableTable } from '@/components/page/SortableTable';
import { amountKey } from '@/components/page/sortKey';
import { ProblemMessage } from '@/components/ProblemMessage';
import { buttonVariants } from '@/components/ui/button';
import { money, percent } from '../loans/format';
import { PROPERTIES, PROPERTIES_RENTAL, type PropertySummary } from './api';
import { typeText } from './format';
import '@/locales/properties';
import '@/locales/forms';
import { Loading, OverviewSkeleton, TableSkeleton } from '@/components/page/skeletons';

const OverviewTab = lazy(() => import('./overview/OverviewTab').then((m) => ({ default: m.OverviewTab })));

const RentalTab = lazy(() => import('./overview/RentalTab').then((m) => ({ default: m.RentalTab })));

const CostsTab = lazy(() => import('./overview/CostsTab').then((m) => ({ default: m.CostsTab })));

const ValuationTab = lazy(() => import('./overview/ValuationTab').then((m) => ({ default: m.ValuationTab })));

const WITH_RENTAL = ['overview', 'rental', 'costs', 'valuation', 'list'] as const;
const WITHOUT_RENTAL = ['overview', 'costs', 'valuation', 'list'] as const;

/**
 * Properties dashboard §3.1, §4.3, §5.3 and §6.3: the overview opens first, « Location » while a property is let, « Charges et
 * coûts », « Valorisation »; the list under `?tab=list`.
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

/** The properties with their value today and their net value (3D1 design §4). */
function ListTab({ scope, add }: { scope: 'me' | 'household'; add: ReactNode }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const answer = $api.useQuery('get', PROPERTIES, { params: { query: { scope } } });
  const items = answer.data?.items;

  if (answer.error) return <ProblemMessage problem={present(toProblem(answer.error))} />;
  if (!items)
    return (
      <Loading label={t('properties.loading')}><TableSkeleton columns={6} /></Loading>
    );
  if (items.length === 0) return <EmptyState module="/properties" title={t('properties.empty_title')} body={t('properties.empty_body')} action={add} />;
  return (
    <SortableTable<PropertySummary> caption={t('properties.list')} rows={items} rowKey={(r) => r.id}
      rowLink={(r) => ({ to: '/properties/$id', params: { id: r.id } })}
      columns={[
        { id: 'name', header: t('lists.col.name'), sort: (r) => r.name, cell: (r) => <span className="inline-flex flex-wrap items-center gap-2"><span className="font-medium">{r.name}</span><AccessBadge access={r.visibility === 'private' ? 'private' : 'shared'} /></span> },
        { id: 'type', header: t('lists.col.kind'), sort: (r) => typeText(t, r.type), cell: (r) => typeText(t, r.type) },
        { id: 'owner', header: t('lists.col.owner'), cell: (r) => <OwnerChips owners={r.owners} /> },
        { id: 'value', header: t('lists.col.value'), align: 'end', sort: (r) => amountKey(r.value), cell: (r) => <Value item={r} /> },
        { id: 'net', header: t('lists.col.net_value'), align: 'end', sort: (r) => amountKey(r.netValue), cell: (r) => r.netValue ? money(r.currency, r.netValue) : <Dash /> },
        // Spec 3D2 §5: the server's fractions in %, « — » when it gives none.
        { id: 'yield', header: t('lists.col.gross_yield'), align: 'end', sort: (r) => amountKey(r.grossYield), cell: (r) => r.grossYield ? percent(t, r.grossYield) : <Dash /> },
      ]}
      mobile={(r) => (
        <div className="space-y-1">
          <div className="flex items-center justify-between gap-2"><span className="font-medium">{r.name}</span><OwnerChips owners={r.owners} /></div>
          <div className="flex flex-wrap items-center justify-between gap-2 text-sm">
            <Value item={r} />
            {r.netValue ? <span className="tabular-nums text-muted-foreground">{money(r.currency, r.netValue)}</span> : null}
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

import { Link, useSearch } from '@tanstack/react-router';
import { Clock } from 'lucide-react';
import { lazy, Suspense, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import type { PropertiesSearch, RootSearch } from '@/app/router';
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
import { Skeleton } from '@/components/ui/skeleton';
import { money, percent } from '../loans/format';
import { PROPERTIES, type PropertySummary } from './api';
import { typeText } from './format';
import '@/locales/properties';

const OverviewTab = lazy(() => import('./overview/OverviewTab').then((m) => ({ default: m.OverviewTab })));

const TABS = ['overview', 'list'] as const;

/** Properties dashboard §3.1: the overview opens first; the list keeps its table under `?tab=list`. */
export function PropertiesPage() {
  const { t } = useTranslation();
  const search: RootSearch & PropertiesSearch = useSearch({ strict: false });
  const scope = search.scope === 'me' ? 'me' : 'household';
  const [tab, setTab] = useDetailTab(TABS);
  const add = <Link to="/properties/new" className={buttonVariants()}>{t('properties.add')}</Link>;
  return (
    <div className="space-y-4">
      <PageHeader title={t('properties.title')} actions={add} />
      <DetailTabs value={tab} onChange={setTab} tabs={[
        { id: 'overview', label: t('properties_overview.tab_overview'),
          content: <Suspense fallback={<Skeleton className="h-64 w-full" />}><OverviewTab scope={scope} span={search.history ?? '3y'} /></Suspense> },
        { id: 'list', label: t('properties_overview.tab_list'), content: <ListTab scope={scope} add={add} /> },
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
      <ul aria-busy="true" aria-label={t('properties.loading')} className="space-y-2">
        {[0, 1, 2].map((i) => <li key={i} className="h-16 animate-pulse rounded-md border bg-muted" />)}
      </ul>
    );
  if (items.length === 0) return <EmptyState title={t('properties.empty_title')} body={t('properties.empty_body')} action={add} />;
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

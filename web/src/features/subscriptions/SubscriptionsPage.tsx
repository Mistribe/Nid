import { keepPreviousData } from '@tanstack/react-query';
import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import { ChevronDownIcon } from 'lucide-react';
import { lazy, Suspense, useId, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import type { RootSearch, SubscriptionsSearch } from '@/app/router';
import { EmptyState } from '@/components/EmptyState';
import { formatLocalDate, LocalDate } from '@/components/LocalDate';
import { Money, type MoneyValue } from '@/components/Money';
import { OwnerChips } from '@/components/OwnerChips';
import { Segmented } from '@/components/form/Segmented';
import { PageHeader } from '@/components/page/PageHeader';
import { DetailTabs, useDetailTab } from '@/components/page/DetailTabs';
import { SortableTable } from '@/components/page/SortableTable';
import { amountKey } from '@/components/page/sortKey';
import { StatLine } from '@/components/page/StatLine';
import { ProblemMessage } from '@/components/ProblemMessage';
import { ProviderIcon } from '@/components/ProviderIcon';
import { Button, buttonVariants } from '@/components/ui/button';
import { DropdownMenu, DropdownMenuCheckboxItem, DropdownMenuContent, DropdownMenuTrigger } from '@/components/ui/dropdown-menu';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Skeleton } from '@/components/ui/skeleton';
import type { components } from '@/api/schema';
import { LABELS, LIST, PERSONS, type ListResponse, type Summary } from './api';
import { labelParam, labelsOf } from './filter';

type Group = components['schemas']['GroupView'];
type GroupBy = NonNullable<SubscriptionsSearch['group']>;

/** One line of the list: a subscription, or a group of several (its subtotal from the server, its subscriptions under it). */
type Row = { key: string; name: string; subtitle?: string; items: Summary[]; monthly?: MoneyValue | null; annual?: MoneyValue | null; children?: Row[] };

const itemRow = (item: Summary, key = item.id, figures: Pick<Row, 'monthly' | 'annual'> = { monthly: item.amortizedCostConverted, annual: item.annualCostConverted }): Row =>
  ({ key, name: item.displayName, items: [item], ...figures });

/** Subscriptions dashboard design §3.3: a group of one reads as its subscription (with the group's figures), groups as the server sorts them. */
function rowsOf(data: ListResponse, nameOf: (g: Group) => string, groupBy: GroupBy): Row[] {
  if (data.groups.length === 0) return data.items.map((item) => itemRow(item));
  const byId = new Map(data.items.map((item) => [item.id, item]));
  return data.groups.map((g) => {
    // Each child carries the figures the group counts: the person's share under a person, the full amount otherwise.
    const members = g.items.flatMap((gi) => {
      const item = byId.get(gi.id);
      return item ? [{ item, figures: { monthly: gi.amortizedCostConverted, annual: gi.annualCostConverted } }] : [];
    });
    const items = members.map((m) => m.item);
    // A subscription shared by two persons sits under both: its key carries the group's.
    if (members.length === 1) {
      const row = itemRow(items[0]!, `${g.key}/${items[0]!.id}`, members[0]!.figures);
      // The figures are the person's share: the row says whose, the subscription under it.
      return groupBy === 'person' ? { ...row, name: nameOf(g), subtitle: row.name } : row;
    }
    return {
      key: g.key, name: nameOf(g), items, monthly: g.amortizedCostConverted, annual: g.annualCostConverted,
      children: members.map((m) => itemRow(m.item, `${g.key}/${m.item.id}`, m.figures)),
    };
  });
}

const OverviewTab = lazy(() => import('./overview/OverviewTab').then((m) => ({ default: m.OverviewTab })));
const CalendarTab = lazy(() => import('./calendar/CalendarTab').then((m) => ({ default: m.CalendarTab })));

/** Subscriptions dashboard design §4.2: the overview opens first; the list keeps its filters under `?tab=list`. */
export function SubscriptionsPage() {
  const { t } = useTranslation();
  const search: RootSearch = useSearch({ strict: false });
  const [tab, setTab] = useDetailTab(['overview', 'list', 'calendar'] as const);
  const scope = search.scope === 'me' ? 'me' : 'household';
  const add = <Link to="/subscriptions/new" className={buttonVariants()}>{t('subscriptions.add')}</Link>;
  return (
    <div className="space-y-4">
      <PageHeader title={t('subscriptions.title')} actions={add} />
      <DetailTabs value={tab} onChange={setTab} tabs={[
        { id: 'overview', label: t('subscriptions.tab_overview'),
          content: <Suspense fallback={<Skeleton className="h-64 w-full" />}><OverviewTab scope={scope} /></Suspense> },
        { id: 'list', label: t('subscriptions.tab_list'), content: <ListTab scope={scope} /> },
        { id: 'calendar', label: t('subscriptions.tab_calendar'),
          content: <Suspense fallback={<Skeleton className="h-64 w-full" />}><CalendarTab scope={scope} /></Suspense> },
      ]} />
    </div>
  );
}

/** Subscriptions dashboard design §3: scope, name, status, labels and grouping all go to the server, which groups and totals. */
function ListTab({ scope }: { scope: 'household' | 'me' }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const search: SubscriptionsSearch = useSearch({ strict: false });
  const navigate = useNavigate();
  const searchId = useId();
  const statusId = useId();
  const groupId = useId();
  const labelsId = useId();
  const totalsId = useId();
  const status = search.status ?? 'active';
  const groupBy: GroupBy = search.group ?? 'provider';

  const labels = $api.useQuery('get', LABELS);
  const offered = labels.data ?? [];
  // Until the labels are read, the address is sent as it is; then an unknown key is dropped.
  const active = labelsOf(search.label).filter((key) => !labels.data || offered.some((label) => label.key === key));
  const answer = $api.useQuery('get', LIST, {
    params: { query: { scope, status, group: groupBy, ...(search.q ? { q: search.q } : {}), ...(active.length > 0 ? { label: labelParam(active) } : {}) } },
  }, { placeholderData: keepPreviousData });
  const persons = $api.useQuery('get', PERSONS);
  // An empty answer with no filter of the person's: are there ended subscriptions, or none at all?
  const bare = !search.q && active.length === 0 && !search.status;
  const everything = $api.useQuery('get', LIST, { params: { query: { scope, status: 'all' } } },
    { enabled: bare && answer.data?.items.length === 0 });

  const setSearch = (change: SubscriptionsSearch) =>
    void navigate({ to: '.', search: (prev) => ({ ...prev, ...change }), replace: true });
  const toggle = (key: string) =>
    setSearch({ label: labelParam(active.includes(key) ? active.filter((k) => k !== key) : [...active, key]) });

  const data = answer.data;
  // Server totals only (no client sum): a StatLine at the top of the list.
  const totals = data && data.items.length > 0 ? (
    <section aria-labelledby={totalsId} className="space-y-1">
      <h2 id={totalsId} className="text-sm font-semibold">{t('subscriptions.household_totals')}</h2>
      <StatLine items={[
        { label: t('subscriptions.per_month'), value: <Amount value={data.householdAmortizedCostConverted} /> },
        { label: t('subscriptions.per_year'), value: <Amount value={data.householdAnnualCostConverted} /> },
        { label: t('subscriptions.cash_month'), value: <Amount value={data.householdCashCostConverted} /> },
      ]} />
      {data.sharedPurchaseIds.length > 0 ? <p className="text-sm text-muted-foreground">{t('subscriptions.shared_purchases_note')}</p> : null}
    </section>
  ) : null;

  if (answer.error) return <ProblemMessage problem={present(toProblem(answer.error))} />;

  const filters = (
    <div className="flex flex-wrap items-end gap-3">
      <div className="space-y-1">
        <Label htmlFor={searchId}>{t('subscriptions.search')}</Label>
        <Input id={searchId} type="search" value={search.q ?? ''} onChange={(e) => setSearch({ q: e.target.value || undefined })} />
      </div>
      <div className="space-y-1">
        <Label id={statusId}>{t('subscriptions.status_label')}</Label>
        <div>
          <Segmented aria-labelledby={statusId} value={status}
            onValueChange={(v) => setSearch({ status: v === 'ended' || v === 'all' ? v : undefined })}
            options={[
              { value: 'active', label: t('subscriptions.status_active') },
              { value: 'ended', label: t('subscriptions.status_ended') },
              { value: 'all', label: t('subscriptions.status_all') },
            ]} />
        </div>
      </div>
      <div className="space-y-1">
        <Label id={groupId}>{t('subscriptions.group_by')}</Label>
        <div>
          <Segmented aria-labelledby={groupId} value={groupBy}
            onValueChange={(v) => setSearch({ group: v === 'label' || v === 'person' || v === 'none' ? v : undefined })}
            options={[
              { value: 'provider', label: t('subscriptions.group_provider') },
              { value: 'label', label: t('subscriptions.group_label') },
              { value: 'person', label: t('subscriptions.group_person') },
              { value: 'none', label: t('subscriptions.group_none') },
            ]} />
        </div>
      </div>
      {offered.length > 0 ? (
        <div className="flex items-center gap-2">
          <DropdownMenu modal={false}>
            <DropdownMenuTrigger asChild>
              <Button id={labelsId} type="button" variant="outline">
                {t('subscriptions.labels')}{active.length > 0 ? ` (${active.length})` : ''}
                <ChevronDownIcon aria-hidden="true" />
              </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent align="start">
              {offered.map((label) => (
                <DropdownMenuCheckboxItem key={label.key} checked={active.includes(label.key)}
                  onCheckedChange={() => toggle(label.key)} onSelect={(e) => e.preventDefault()}>
                  <span aria-hidden="true" className="size-2.5 rounded-full" style={{ backgroundColor: label.color }} />
                  {label.name}
                </DropdownMenuCheckboxItem>
              ))}
            </DropdownMenuContent>
          </DropdownMenu>
          {active.length > 0 ? (
            <Button type="button" size="sm" variant="ghost" onClick={() => setSearch({ label: undefined })}>{t('subscriptions.labels_clear')}</Button>
          ) : null}
        </div>
      ) : null}
    </div>
  );

  const page = (content: ReactNode) => <>{totals}{filters}{content}</>;

  if (!data || (data.items.length === 0 && bare && !everything.data && !everything.error))
    return page(
      <ul aria-busy="true" aria-label={t('subscriptions.loading')} className="space-y-2">
        {[0, 1, 2].map((i) => <li key={i} className="h-16 shimmer rounded-md border bg-muted" />)}
      </ul>,
    );

  if (data.items.length === 0 && bare && everything.data?.items.length === 0)
    return page(
      <EmptyState title={t('subscriptions.empty_title')} body={t('subscriptions.empty_body')}
        action={<Link to="/subscriptions/new" className={buttonVariants()}>{t('subscriptions.empty_action')}</Link>} />,
    );

  const nameOf = (g: Group) => g.name
    ?? (g.key === 'none' ? t(groupBy === 'label' ? 'subscriptions.no_label' : 'subscriptions.no_provider')
      : persons.data?.find((p) => p.id === g.key)?.displayName ?? '');
  const shown = rowsOf(data, nameOf, groupBy);
  const nameHeader = { provider: 'subscriptions.group_provider', label: 'subscriptions.group_label', person: 'subscriptions.group_person', none: 'lists.col.name' } as const;
  return page(shown.length === 0 ? <p>{t('subscriptions.no_match')}</p> : (
    <SortableTable<Row> caption={t('subscriptions.list_label')} rows={shown} rowKey={(r) => r.key}
      rowLink={(r) => ({ to: '/subscriptions/$id', params: { id: r.items[0]!.id } })}
      rowClassName={(r) => (r.children ? 'bg-muted/50' : undefined)}
      subRows={(r) => r.children} expandLabel={(r) => t('subscriptions.expand', { name: r.name })}
      columns={[
        { id: 'name', header: t(nameHeader[groupBy]), sort: (r) => r.name, cell: (r) => <Name row={r} /> },
        { id: 'owner', header: t('lists.col.owner'), cell: (r) => <Owners row={r} /> },
        { id: 'labels', header: t('subscriptions.labels'), cell: (r) => <Labels row={r} /> },
        { id: 'next', header: t('lists.col.next_payment'), sort: (r) => nextOf(r) ?? expiryOf(r) ?? Number.NaN, cell: (r) => <Next row={r} /> },
        { id: 'monthly', header: t('subscriptions.per_month'), align: 'end', sort: (r) => amountKey(r.monthly?.amount), cell: (r) => <Amount value={r.monthly} /> },
        { id: 'annual', header: t('subscriptions.per_year'), align: 'end', sort: (r) => amountKey(r.annual?.amount), cell: (r) => <Amount value={r.annual} /> },
      ]}
      mobile={(r) => (
        <div className="flex items-center justify-between gap-2">
          <Name row={r} />
          <Amount value={r.monthly} />
        </div>
      )} />
  ));
}

/** The nearest next charge of the row's subscriptions: a date, chosen in the browser (no amount involved). */
const nextOf = (r: Row) => r.items.flatMap((item) => item.nextChargeOn ?? []).sort()[0];
const expiryOf = (r: Row) => (r.children ? undefined : r.items[0]!.expiresOn ?? undefined);

/** A server figure in the reference currency; « — » when a rate is missing. */
function Amount({ value }: { value?: MoneyValue | null }) {
  return value ? <Money value={value} tabular /> : <span>—</span>;
}

function Name({ row }: { row: Row }) {
  const { t } = useTranslation();
  // A group by label or person mixes providers: its icon is then its name's monogram.
  const provider = row.items.every((item) => item.provider?.key === row.items[0]!.provider?.key) ? row.items[0]!.provider : null;
  return (
    <span className="inline-flex items-center gap-3">
      <ProviderIcon name={provider?.name ?? row.name} iconUrl={provider?.iconUrl} color={provider?.labels[0]?.color} />
      <span>
        <span className="font-medium">{row.name}</span>
        {row.children || row.subtitle
          ? <span className="text-muted-foreground"> · {row.children ? t('subscriptions.count', { count: row.items.length }) : row.subtitle}</span>
          : null}
      </span>
    </span>
  );
}

/** The owners of the row's subscriptions, once each; a group shows no share. */
function Owners({ row }: { row: Row }) {
  if (!row.children) return <OwnerChips owners={row.items[0]!.owners} />;
  const owners = [...new Map(row.items.flatMap((item) => item.owners).map((o) => [o.personId, o])).values()];
  return <OwnerChips owners={owners} shares={false} />;
}

function Labels({ row }: { row: Row }) {
  const labels = [...new Map(row.items.flatMap((item) => item.provider?.labels ?? []).map((l) => [l.key, l])).values()];
  return (
    <span className="inline-flex flex-wrap gap-x-3 gap-y-1 text-sm">
      {labels.map((label) => (
        <span key={label.key} className="inline-flex items-center gap-1.5">
          <span aria-hidden="true" className="size-2 rounded-full" style={{ backgroundColor: label.color }} />
          {label.name}
        </span>
      ))}
    </span>
  );
}

/** The next charge, or the day a lone subscription expires when nothing is due. */
function Next({ row }: { row: Row }) {
  const { t } = useTranslation();
  const next = nextOf(row);
  const expires = expiryOf(row);
  if (next) return <LocalDate value={next} style="short" />;
  if (expires) return <>{t('subscriptions.expires', { date: formatLocalDate(expires, 'short') })}</>;
  return null;
}

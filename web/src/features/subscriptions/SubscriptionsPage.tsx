import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import { ChevronDownIcon } from 'lucide-react';
import { useId } from 'react';
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
import { SortableTable } from '@/components/page/SortableTable';
import { amountKey } from '@/components/page/sortKey';
import { StatLine } from '@/components/page/StatLine';
import { ProblemMessage } from '@/components/ProblemMessage';
import { ProviderIcon } from '@/components/ProviderIcon';
import { Button, buttonVariants } from '@/components/ui/button';
import { DropdownMenu, DropdownMenuCheckboxItem, DropdownMenuContent, DropdownMenuTrigger } from '@/components/ui/dropdown-menu';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { LIST, type Summary } from './api';
import { filterSubscriptions, labelParam, labelsInUse, labelsOf } from './filter';
import { todayIso } from './today';

/** 1E2a design §3: scope (the shell's switch) goes to the server, name and status filter the answer in the browser. */
export function SubscriptionsPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const search: RootSearch & SubscriptionsSearch = useSearch({ strict: false });
  const navigate = useNavigate();
  const searchId = useId();
  const statusId = useId();
  const labelsId = useId();
  const totalsId = useId();
  const scope = search.scope === 'me' ? 'me' : 'household';
  const answer = $api.useQuery('get', LIST, { params: { query: { scope } } });

  const setSearch = (change: SubscriptionsSearch) =>
    void navigate({ to: '.', search: (prev) => ({ ...prev, ...change }), replace: true });

  const offered = labelsInUse(answer.data?.items ?? []);
  const active = labelsOf(search.label).filter((key) => offered.some((label) => label.key === key));
  const toggle = (key: string) =>
    setSearch({ label: labelParam(active.includes(key) ? active.filter((k) => k !== key) : [...active, key]) });

  const add = <Link to="/subscriptions/new" className={buttonVariants()}>{t('subscriptions.add')}</Link>;
  const data = answer.data;
  // Server totals only (no client sum): a StatLine under the title.
  const totals = data && data.items.length > 0 ? (
    <section aria-labelledby={totalsId} className="space-y-1">
      <h2 id={totalsId} className="text-sm font-semibold">{t('subscriptions.household_totals')}</h2>
      <StatLine items={[
        { label: t('lists.col.cash'), value: <Moneys values={data.householdCashCost} /> },
        { label: t('lists.col.amortized'), value: <Moneys values={data.householdAmortizedCost} /> },
      ]} />
      {data.sharedPurchaseIds.length > 0 ? <p className="text-sm text-muted-foreground">{t('subscriptions.shared_purchases_note')}</p> : null}
    </section>
  ) : undefined;
  const header = <PageHeader title={t('subscriptions.title')} actions={add}>{totals}</PageHeader>;

  if (answer.error) return <div className="space-y-4">{header}<ProblemMessage problem={present(toProblem(answer.error))} /></div>;

  const filters = (
    <div className="flex flex-wrap items-end gap-3">
      <div className="space-y-1">
        <Label htmlFor={searchId}>{t('subscriptions.search')}</Label>
        <Input id={searchId} type="search" value={search.q ?? ''} onChange={(e) => setSearch({ q: e.target.value || undefined })} />
      </div>
      <div className="space-y-1">
        <Label id={statusId}>{t('subscriptions.status_label')}</Label>
        <div>
          <Segmented aria-labelledby={statusId} value={search.status ?? 'all'}
            onValueChange={(v) => setSearch({ status: v === 'active' || v === 'ended' ? v : undefined })}
            options={[
              { value: 'all', label: t('subscriptions.status_all') },
              { value: 'active', label: t('subscriptions.status_active') },
              { value: 'ended', label: t('subscriptions.status_ended') },
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

  if (!data)
    return (
      <div className="space-y-4">
        {header}
        <ul aria-busy="true" aria-label={t('subscriptions.loading')} className="space-y-2">
          {[0, 1, 2].map((i) => <li key={i} className="h-16 animate-pulse rounded-md border bg-muted" />)}
        </ul>
      </div>
    );

  if (data.items.length === 0)
    return (
      <div className="space-y-4">
        {header}
        {filters}
        <EmptyState title={t('subscriptions.empty_title')} body={t('subscriptions.empty_body')}
          action={<Link to="/subscriptions/new" className={buttonVariants()}>{t('subscriptions.empty_action')}</Link>} />
      </div>
    );

  const shown = filterSubscriptions(data.items, { ...search, labels: active }, todayIso());
  return (
    <div className="space-y-4">
      {header}
      {filters}
      {shown.length === 0 ? <p>{t('subscriptions.no_match')}</p> : (
        <SortableTable<Summary> caption={t('subscriptions.list_label')} rows={shown} rowKey={(r) => r.id}
          rowLink={(r) => ({ to: '/subscriptions/$id', params: { id: r.id } })}
          columns={[
            { id: 'name', header: t('lists.col.name'), sort: (r) => r.displayName, cell: (r) => <Name item={r} /> },
            { id: 'owner', header: t('lists.col.owner'), cell: (r) => <OwnerChips owners={r.owners} /> },
            { id: 'cash', header: t('lists.col.cash'), align: 'end', sort: (r) => amountKey(r.cashCost[0]?.amount), cell: (r) => <Moneys values={r.cashCost} /> },
            { id: 'amortized', header: t('lists.col.amortized'), align: 'end', sort: (r) => amountKey(r.amortizedCost[0]?.amount), cell: (r) => <Moneys values={r.amortizedCost} /> },
            { id: 'next', header: t('lists.col.next_payment'), sort: (r) => r.nextChargeOn ?? r.expiresOn ?? '', cell: (r) => <Next item={r} /> },
          ]}
          mobile={(r) => (
            <div className="space-y-1">
              <div className="flex items-center justify-between gap-2"><Name item={r} /><OwnerChips owners={r.owners} /></div>
              <div className="flex flex-wrap items-center justify-between gap-2 text-sm">
                <Moneys values={r.cashCost} />
                <span className="text-muted-foreground"><Next item={r} /></span>
              </div>
            </div>
          )} />
      )}
    </div>
  );
}

function Moneys({ values }: { values: MoneyValue[] }) {
  return <span className="inline-flex flex-col items-end">{values.map((m) => <Money key={m.currency} value={m} tabular />)}</span>;
}

function Name({ item }: { item: Summary }) {
  return (
    <span className="inline-flex items-center gap-3">
      <ProviderIcon name={item.provider?.name ?? item.displayName} iconUrl={item.provider?.iconUrl} color={item.provider?.labels[0]?.color} />
      <span className="font-medium">{item.displayName}</span>
    </span>
  );
}

/** The next charge, or the day it expires when nothing is due. */
function Next({ item }: { item: Summary }) {
  const { t } = useTranslation();
  if (item.nextChargeOn) return <LocalDate value={item.nextChargeOn} style="short" />;
  if (item.expiresOn) return <>{t('subscriptions.expires', { date: formatLocalDate(item.expiresOn, 'short') })}</>;
  return null;
}

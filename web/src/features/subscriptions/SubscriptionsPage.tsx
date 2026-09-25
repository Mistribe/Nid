import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import { AlertTriangle } from 'lucide-react';
import { useId } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import type { RootSearch, SubscriptionsSearch } from '@/app/router';
import { EmptyState } from '@/components/EmptyState';
import { formatLocalDate } from '@/components/LocalDate';
import { Money } from '@/components/Money';
import { ProblemMessage } from '@/components/ProblemMessage';
import { ProviderIcon } from '@/components/ProviderIcon';
import { LIST, type Summary } from './api';
import { filterSubscriptions } from './filter';
import { todayIso } from './today';

/** 1E2a design §3: scope (the shell's switch) goes to the server, name and status filter the answer in the browser. */
export function SubscriptionsPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const search: RootSearch & SubscriptionsSearch = useSearch({ strict: false });
  const navigate = useNavigate();
  const searchId = useId();
  const statusId = useId();
  const totalsId = useId();
  const scope = search.scope === 'me' ? 'me' : 'household';
  const answer = $api.useQuery('get', LIST, { params: { query: { scope } } });

  const setSearch = (change: SubscriptionsSearch) =>
    void navigate({ to: '.', search: (prev: RootSearch & SubscriptionsSearch) => ({ ...prev, ...change }), replace: true });

  const header = (
    <div className="flex flex-wrap items-center justify-between gap-3">
      <h1 className="text-2xl font-semibold">{t('subscriptions.title')}</h1>
      <Link to="/subscriptions/new" className="rounded-md bg-primary px-3 py-2 text-sm text-primary-foreground">{t('subscriptions.add')}</Link>
    </div>
  );

  if (answer.error) return <>{header}<ProblemMessage problem={present(toProblem(answer.error))} /></>;

  const filters = (
    <div className="flex flex-wrap items-end gap-3">
      <div className="space-y-1">
        <label htmlFor={searchId} className="block text-sm">{t('subscriptions.search')}</label>
        <input id={searchId} type="search" value={search.q ?? ''} onChange={(e) => setSearch({ q: e.target.value || undefined })}
          className="rounded-md border px-3 py-1.5" />
      </div>
      <div className="space-y-1">
        <label htmlFor={statusId} className="block text-sm">{t('subscriptions.status_label')}</label>
        <select id={statusId} value={search.status ?? ''} className="rounded-md border px-3 py-1.5"
          onChange={(e) => setSearch({ status: e.target.value === 'active' || e.target.value === 'ended' ? e.target.value : undefined })}>
          <option value="">{t('subscriptions.status_all')}</option>
          <option value="active">{t('subscriptions.status_active')}</option>
          <option value="ended">{t('subscriptions.status_ended')}</option>
        </select>
      </div>
    </div>
  );

  if (!answer.data)
    return (
      <>
        {header}
        <ul aria-busy="true" aria-label={t('subscriptions.loading')} className="space-y-2">
          {[0, 1, 2].map((i) => <li key={i} className="h-16 animate-pulse rounded-md border bg-muted" />)}
        </ul>
      </>
    );

  const data = answer.data;
  if (data.items.length === 0)
    return (
      <>
        {header}
        {filters}
        <EmptyState title={t('subscriptions.empty_title')} body={t('subscriptions.empty_body')}
          action={<Link to="/subscriptions/new" className="rounded-md bg-primary px-3 py-2 text-primary-foreground">{t('subscriptions.empty_action')}</Link>} />
      </>
    );

  const shown = filterSubscriptions(data.items, search, todayIso());
  return (
    <div className="space-y-4">
      {header}
      {filters}
      <section aria-labelledby={totalsId} className="rounded-md border p-3">
        <h2 id={totalsId} className="text-sm font-semibold">{t('subscriptions.household_totals')}</h2>
        <p className="flex flex-wrap gap-x-4">
          {data.householdCashCost.map((m) => <span key={m.currency}><Money value={m} /></span>)}
        </p>
        {data.sharedPurchaseIds.length > 0 ? <p className="text-sm text-muted-foreground">{t('subscriptions.shared_purchases_note')}</p> : null}
      </section>
      {shown.length === 0 ? <p>{t('subscriptions.no_match')}</p> : (
        <ul aria-label={t('subscriptions.list_label')} className="space-y-2">
          {shown.map((item) => <Row key={item.id} item={item} />)}
        </ul>
      )}
    </div>
  );
}

function Row({ item }: { item: Summary }) {
  const { t } = useTranslation();
  return (
    <li className="rounded-md border">
      <Link to="/subscriptions/$id" params={{ id: item.id }} className="flex flex-wrap items-center gap-3 p-3 hover:bg-muted">
        <ProviderIcon name={item.provider?.name ?? item.displayName} iconUrl={item.provider?.iconUrl} color={item.provider?.labels[0]?.color} />
        <span className="min-w-40 flex-1 font-medium">{item.displayName}</span>
        <span className="text-sm">
          <span className="text-muted-foreground">{t('subscriptions.cash')} </span>
          {item.cashCost.map((m) => <Money key={m.currency} value={m} tabular />)}
        </span>
        <span className="text-sm">
          <span className="text-muted-foreground">{t('subscriptions.amortized')} </span>
          {item.amortizedCost.map((m) => <Money key={m.currency} value={m} tabular />)}
        </span>
        <span className="text-sm">
          {item.nextChargeOn
            ? t('subscriptions.next_charge', { date: formatLocalDate(item.nextChargeOn) })
            : item.expiresOn ? t('subscriptions.expires', { date: formatLocalDate(item.expiresOn) }) : null}
        </span>
      </Link>
      {item.findings.length > 0 ? (
        <ul aria-label={t('subscriptions.findings')} className="flex flex-wrap gap-2 px-3 pb-3">
          {item.findings.map((f) => (
            <li key={`${f.code}-${f.reference ?? ''}`} className="inline-flex items-center gap-1 rounded-md border px-2 py-0.5 text-sm">
              <AlertTriangle aria-hidden="true" className="size-4" />
              {f.message}
            </li>
          ))}
        </ul>
      ) : null}
    </li>
  );
}

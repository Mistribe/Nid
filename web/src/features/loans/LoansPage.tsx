import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import { Archive } from 'lucide-react';
import { lazy, Suspense } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import type { LoansSearch, RootSearch } from '@/app/router';
import { AccessBadge } from '@/components/AccessBadge';
import { EmptyState } from '@/components/EmptyState';
import { LocalDate } from '@/components/LocalDate';
import { OwnerChips } from '@/components/OwnerChips';
import { DetailTabs, useDetailTab } from '@/components/page/DetailTabs';
import { PageHeader } from '@/components/page/PageHeader';
import { SortableTable } from '@/components/page/SortableTable';
import { amountKey } from '@/components/page/sortKey';
import { SwitchField } from '@/components/page/SwitchField';
import { ProblemMessage } from '@/components/ProblemMessage';
import { buttonVariants } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { LOANS, OVERVIEW, type LoanSummary } from './api';
import { money, percent } from './format';
import '@/locales/loans';
import '@/locales/forms';

function useLoans(scope: 'me' | 'household', ended: boolean, enabled = true) {
  const { $api } = useApi();
  return $api.useQuery('get', LOANS, { params: { query: { scope, ...(ended ? { ended: true } : {}) } } }, { enabled });
}

const OverviewTab = lazy(() => import('./overview/OverviewTab').then((m) => ({ default: m.OverviewTab })));

const EarlyTab = lazy(() => import('./early/EarlyTab').then((m) => ({ default: m.EarlyTab })));

const WITH_EARLY = ['overview', 'list', 'early'] as const;
const WITHOUT_EARLY = ['overview', 'list'] as const;

/**
 * Loans dashboard §3.1 and §5.3: the overview opens first; the list keeps its switch of the ended loans under `?tab=list`;
 * « Rembourser plus tôt » (`?tab=early`) only while a loan is running. The overview's answer is shared with its tab.
 */
export function LoansPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const search: RootSearch = useSearch({ strict: false });
  const scope = search.scope === 'me' ? 'me' : 'household';
  const overview = $api.useQuery('get', OVERVIEW, { params: { query: { scope } } });
  const running = overview.data && overview.data.runningCount > 0 ? overview.data : null;
  const [tab, setTab] = useDetailTab<(typeof WITH_EARLY)[number]>(running ? WITH_EARLY : WITHOUT_EARLY);
  const fallback = <Skeleton className="h-64 w-full" />;
  return (
    <div className="space-y-4">
      <PageHeader title={t('loans.title')} actions={<Link to="/loans/new" className={buttonVariants()}>{t('loans.add')}</Link>} />
      <DetailTabs value={tab} onChange={setTab} tabs={[
        { id: 'overview', label: t('loans_overview.tab_overview'), content: <Suspense fallback={fallback}><OverviewTab scope={scope} /></Suspense> },
        { id: 'list', label: t('loans_overview.tab_list'), content: <ListTab scope={scope} /> },
        ...(running ? [{ id: 'early' as const, label: t('loans_early.tab'),
          content: <Suspense fallback={fallback}><EarlyTab scope={scope} overview={running} /></Suspense> }] : []),
      ]} />
    </div>
  );
}

/** The loans with their balance, next payment, rate and end (3B1 design §5). */
function ListTab({ scope }: { scope: 'me' | 'household' }) {
  const { t } = useTranslation();
  const search: RootSearch & LoansSearch = useSearch({ strict: false });
  const navigate = useNavigate();
  const ended = search.ended === 1;
  const answer = useLoans(scope, ended);
  // An empty list without the ended loans asks once more with them (ended=true returns running and ended alike),
  // to tell « nothing yet » from « nothing running » (spec §5).
  const withEnded = useLoans(scope, true, !ended && answer.data?.items.length === 0);
  const setEnded = (checked: boolean) =>
    void navigate({ to: '/loans', search: (prev: RootSearch & LoansSearch) => ({ ...prev, ended: checked ? (1 as const) : undefined }), replace: true });

  // One stable frame whatever the answer: the switch must survive the reload its own change causes (focus).
  return (
    <>
      <SwitchField label={t('loans.show_ended')} checked={ended} onCheckedChange={setEnded} />
      <Body answer={answer} withEnded={ended ? null : withEnded} />
    </>
  );
}

function Body({ answer, withEnded }: { answer: ReturnType<typeof useLoans>; withEnded: ReturnType<typeof useLoans> | null }) {
  const { t } = useTranslation();
  if (answer.error) return <ProblemMessage problem={present(toProblem(answer.error))} />;

  const empty = answer.data?.items.length === 0;
  // The second request decides which empty state; a failure of it falls back to « nothing yet ».
  const deciding = empty && withEnded !== null && !withEnded.data && !withEnded.error;
  if (!answer.data || deciding)
    return (
      <ul aria-busy="true" aria-label={t('loans.loading')} className="space-y-2">
        {[0, 1, 2].map((i) => <li key={i} className="h-16 shimmer rounded-md border bg-muted" />)}
      </ul>
    );

  if (empty && (withEnded?.data?.items.length ?? 0) > 0)
    return (
      <EmptyState title={t('loans.none_running_title')} body={t('loans.none_running_body')}
        action={<Link to="/loans" search={(prev: RootSearch & LoansSearch) => ({ ...prev, ended: 1 as const })} className={buttonVariants({ variant: 'outline' })}>{t('loans.see_ended')}</Link>} />
    );
  if (empty)
    return (
      <EmptyState title={t('loans.empty_title')} body={t('loans.empty_body')}
        action={<Link to="/loans/new" className={buttonVariants()}>{t('loans.add')}</Link>} />
    );

  return (
    <SortableTable<LoanSummary> caption={t('loans.list')} rows={answer.data.items} rowKey={(r) => r.id}
      rowLink={(r) => ({ to: '/loans/$id', params: { id: r.id } })}
      columns={[
        { id: 'name', header: t('lists.col.name'), sort: (r) => r.name, cell: (r) => <Name item={r} /> },
        { id: 'property', header: t('lists.col.property'), sort: (r) => r.propertyName ?? '', cell: (r) => r.propertyName ?? <Dash /> },
        { id: 'owner', header: t('lists.col.owner'), cell: (r) => <OwnerChips owners={r.owners} /> },
        { id: 'remaining', header: t('lists.col.remaining'), align: 'end', sort: (r) => amountKey(r.remainingBalance), cell: (r) => money(r.currency, r.remainingBalance) },
        { id: 'next', header: t('lists.col.next_payment'), sort: (r) => r.nextPaymentOn ?? '',
          cell: (r) => r.nextPaymentOn && r.nextPaymentTotal
            ? <span className="inline-flex flex-col"><LocalDate value={r.nextPaymentOn} style="short" /><span className="text-xs text-muted-foreground tabular-nums">{money(r.currency, r.nextPaymentTotal)}</span></span>
            : <Dash /> },
        // An ended loan has no current rate (precision 9): the server still names its last one.
        { id: 'rate', header: t('lists.col.rate'), align: 'end', sort: (r) => amountKey(r.ended ? null : r.currentRate), cell: (r) => r.currentRate && !r.ended ? percent(t, r.currentRate) : <Dash /> },
        { id: 'end', header: t('lists.col.end'), sort: (r) => r.endDate ?? '', cell: (r) => r.endDate ? <LocalDate value={r.endDate} style="short" /> : <Dash /> },
      ]}
      mobile={(r) => (
        <div className="space-y-1">
          <div className="flex items-center justify-between gap-2"><Name item={r} /><OwnerChips owners={r.owners} /></div>
          <div className="flex flex-wrap items-center justify-between gap-2 text-sm">
            <span className="tabular-nums">{money(r.currency, r.remainingBalance)}</span>
            {r.nextPaymentOn ? <span className="text-muted-foreground"><LocalDate value={r.nextPaymentOn} style="short" /></span> : null}
          </div>
        </div>
      )} />
  );
}

function Dash() {
  const { t } = useTranslation();
  return <span aria-hidden="true">{t('investments.dash')}</span>;
}

function Name({ item }: { item: LoanSummary }) {
  const { t } = useTranslation();
  return (
    <span className="inline-flex flex-col gap-0.5">
      <span className="inline-flex flex-wrap items-center gap-2">
        <span className="font-medium">{item.name}</span>
        {item.ended ? <span className="inline-flex items-center gap-1 text-sm text-muted-foreground"><Archive aria-hidden="true" className="size-4" />{t('loans.ended_mark')}</span> : null}
        <AccessBadge access={item.visibility === 'private' ? 'private' : 'shared'} />
      </span>
      {item.lender ? <span className="text-sm text-muted-foreground">{item.lender}</span> : null}
    </span>
  );
}

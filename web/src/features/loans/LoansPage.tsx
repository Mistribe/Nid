import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import { Archive } from 'lucide-react';
import { useId } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import type { LoansSearch, RootSearch } from '@/app/router';
import { AccessBadge } from '@/components/AccessBadge';
import { EmptyState } from '@/components/EmptyState';
import { formatLocalDate } from '@/components/LocalDate';
import { ProblemMessage } from '@/components/ProblemMessage';
import { LOANS, type LoanSummary } from './api';
import { money } from './format';
import { fractionToPercent } from './rate';

function useLoans(scope: 'me' | 'household', ended: boolean, enabled = true) {
  const { $api } = useApi();
  return $api.useQuery('get', LOANS, { params: { query: { scope, ...(ended ? { ended: true } : {}) } } }, { enabled });
}

/** The loans with their balance, next payment, rate and end (3B1 design §5). */
export function LoansPage() {
  const { t } = useTranslation();
  const search: RootSearch & LoansSearch = useSearch({ strict: false });
  const navigate = useNavigate();
  const endedId = useId();
  const scope = search.scope === 'me' ? 'me' : 'household';
  const ended = search.ended === 1;
  const answer = useLoans(scope, ended);
  // An empty list without the ended loans asks once more with them (ended=true returns running and ended alike),
  // to tell « nothing yet » from « nothing running » (spec §5).
  const withEnded = useLoans(scope, true, !ended && answer.data?.items.length === 0);
  const setEnded = (checked: boolean) =>
    void navigate({ to: '/loans', search: (prev: RootSearch & LoansSearch) => ({ ...prev, ended: checked ? (1 as const) : undefined }), replace: true });

  // One stable frame whatever the answer: the checkbox must survive the reload its own change causes (focus).
  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="text-2xl font-semibold">{t('loans.title')}</h1>
        <Link to="/loans/new" className="rounded-md bg-primary px-3 py-2 text-sm text-primary-foreground">{t('loans.add')}</Link>
      </div>
      <div className="flex items-center gap-2 text-sm">
        <input id={endedId} type="checkbox" checked={ended} onChange={(e) => setEnded(e.target.checked)} />
        <label htmlFor={endedId}>{t('loans.show_ended')}</label>
      </div>
      <Body answer={answer} withEnded={ended ? null : withEnded} />
    </div>
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
        {[0, 1, 2].map((i) => <li key={i} className="h-16 animate-pulse rounded-md border bg-muted" />)}
      </ul>
    );

  if (empty && (withEnded?.data?.items.length ?? 0) > 0)
    return (
      <EmptyState title={t('loans.none_running_title')} body={t('loans.none_running_body')}
        action={<Link to="/loans" search={(prev: RootSearch & LoansSearch) => ({ ...prev, ended: 1 as const })} className="underline">{t('loans.see_ended')}</Link>} />
    );
  if (empty)
    return (
      <EmptyState title={t('loans.empty_title')} body={t('loans.empty_body')}
        action={<Link to="/loans/new" className="rounded-md bg-primary px-3 py-2 text-primary-foreground">{t('loans.add')}</Link>} />
    );

  return (
    <ul aria-label={t('loans.list')} className="space-y-2">
      {answer.data.items.map((item) => <Row key={item.id} item={item} />)}
    </ul>
  );
}

function Row({ item }: { item: LoanSummary }) {
  const { t } = useTranslation();
  return (
    <li className="space-y-1 rounded-md border p-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <span className="flex flex-wrap items-baseline gap-2">
          <Link to="/loans/$id" params={{ id: item.id }} className="font-medium hover:underline">{item.name}</Link>
          {item.lender ? <span className="text-sm text-muted-foreground">{item.lender}</span> : null}
        </span>
        <span className="flex flex-wrap items-center gap-2 text-sm">
          {item.ended ? <span className="flex items-center gap-1"><Archive aria-hidden="true" className="size-4" />{t('loans.ended_mark')}</span> : null}
          <AccessBadge access={item.visibility === 'private' ? 'private' : 'shared'} />
        </span>
      </div>
      <div className="flex flex-wrap gap-x-4 gap-y-1 text-sm tabular-nums">
        <span>{t('loans.remaining', { balance: money(item.currency, item.remainingBalance), principal: money(item.currency, item.principal) })}</span>
        {item.nextPaymentOn && item.nextPaymentTotal ? (
          <span>{t('loans.next_payment', { date: formatLocalDate(item.nextPaymentOn), amount: money(item.currency, item.nextPaymentTotal) })}</span>
        ) : null}
        {/* An ended loan has no current rate (precision 9): the server still names its last one. */}
        {item.currentRate && !item.ended ? <span>{t('loans.rate', { rate: fractionToPercent(item.currentRate) })}</span> : null}
        {item.endDate ? <span>{t('loans.ends_on', { date: formatLocalDate(item.endDate) })}</span> : null}
      </div>
    </li>
  );
}

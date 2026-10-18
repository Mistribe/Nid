import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import { AlertTriangle, Archive } from 'lucide-react';
import { useId } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import type { IncomeSearch, RootSearch } from '@/app/router';
import { AccessBadge } from '@/components/AccessBadge';
import { EmptyState } from '@/components/EmptyState';
import { formatMoney } from '@/components/Money';
import { OwnerChips } from '@/components/OwnerChips';
import { ProblemMessage } from '@/components/ProblemMessage';
import { SOURCES, type IncomeSourceSummary } from './api';
import { kindText } from './format';

function useSources(scope: 'me' | 'household', ended: boolean, enabled = true) {
  const { $api } = useApi();
  return $api.useQuery('get', SOURCES, { params: { query: { scope, ...(ended ? { archived: true } : {}) } } }, { enabled });
}

/** The income sources with their monthly equivalent, running components and gap warnings (2C2a design §4). */
export function IncomeSourcesPage() {
  const { t } = useTranslation();
  const search: RootSearch & IncomeSearch = useSearch({ strict: false });
  const navigate = useNavigate();
  const endedId = useId();
  const scope = search.scope === 'me' ? 'me' : 'household';
  const ended = search.ended === 1;
  const answer = useSources(scope, ended);
  // An empty list without the ended sources asks once more with them (archived=true returns open and ended alike),
  // to tell « nothing yet » from « nothing running » (spec §10.4).
  const withEnded = useSources(scope, true, !ended && answer.data?.items.length === 0);
  const setEnded = (checked: boolean) =>
    void navigate({ to: '/income', search: (prev: RootSearch & IncomeSearch) => ({ ...prev, ended: checked ? (1 as const) : undefined }), replace: true });

  // One stable frame whatever the answer: the checkbox must survive the reload its own change causes (focus).
  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="text-2xl font-semibold">{t('income.title')}</h1>
        <Link to="/income/new" className="rounded-md bg-primary px-3 py-2 text-sm text-primary-foreground">{t('income.add')}</Link>
      </div>
      <div className="flex items-center gap-2 text-sm">
        <input id={endedId} type="checkbox" checked={ended} onChange={(e) => setEnded(e.target.checked)} />
        <label htmlFor={endedId}>{t('income.show_ended')}</label>
      </div>
      <Body answer={answer} withEnded={ended ? null : withEnded} />
    </div>
  );
}

function Body({ answer, withEnded }: { answer: ReturnType<typeof useSources>; withEnded: ReturnType<typeof useSources> | null }) {
  const { t } = useTranslation();
  if (answer.error) return <ProblemMessage problem={present(toProblem(answer.error))} />;

  const empty = answer.data?.items.length === 0;
  // The second request decides which empty state; a failure of it falls back to « nothing yet ».
  const deciding = empty && withEnded !== null && !withEnded.data && !withEnded.error;
  if (!answer.data || deciding)
    return (
      <ul aria-busy="true" aria-label={t('income.loading')} className="space-y-2">
        {[0, 1, 2].map((i) => <li key={i} className="h-16 animate-pulse rounded-md border bg-muted" />)}
      </ul>
    );

  if (empty && (withEnded?.data?.items.length ?? 0) > 0)
    return (
      <EmptyState title={t('income.none_running_title')} body={t('income.none_running_body')}
        action={<Link to="/income" search={(prev: RootSearch & IncomeSearch) => ({ ...prev, ended: 1 as const })} className="underline">{t('income.see_ended')}</Link>} />
    );
  if (empty)
    return (
      <EmptyState title={t('income.empty_title')} body={t('income.empty_body')}
        action={<Link to="/income/new" className="rounded-md bg-primary px-3 py-2 text-primary-foreground">{t('income.add')}</Link>} />
    );

  return (
    <>
      <p className="text-sm text-muted-foreground">{t('income.smoothed')}</p>
      <ul aria-label={t('income.list')} className="space-y-2">
        {answer.data.items.map((item) => <Row key={item.id} item={item} />)}
      </ul>
    </>
  );
}

function Row({ item }: { item: IncomeSourceSummary }) {
  const { t } = useTranslation();
  return (
    <li className="space-y-1 rounded-md border p-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <Link to="/income/$id" params={{ id: item.id }} className="font-medium hover:underline">{item.displayName}</Link>
        <span className="flex flex-wrap items-center gap-2 text-sm text-muted-foreground">
          {item.ended ? <span className="flex items-center gap-1"><Archive aria-hidden="true" className="size-4" />{t('income.ended_mark')}</span> : null}
          <span>{kindText(t, item.kind)}</span>
          {item.payerLabel ? <span>{item.payerLabel}</span> : null}
          <AccessBadge access={item.visibility === 'private' ? 'private' : 'shared'} />
        </span>
      </div>
      <OwnerChips owners={item.owners} />
      <div className="flex flex-wrap items-center gap-3 text-sm">
        <span className="tabular-nums">{t('income.per_month', { amount: formatMoney(item.monthlyEquivalent) })}</span>
        <span className="text-muted-foreground">{t('income.running', { count: item.runningPhases })}</span>
      </div>
      {item.gapSoon.map((label) => (
        <p key={label} role="note" className="flex items-center gap-2 text-sm">
          <AlertTriangle aria-hidden="true" className="size-4 text-status-warning" />
          {t('income.gap_soon', { label })}
        </p>
      ))}
    </li>
  );
}

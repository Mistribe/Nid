import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import { AlertTriangle, Archive } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import type { IncomeSearch, RootSearch } from '@/app/router';
import { AccessBadge } from '@/components/AccessBadge';
import { EmptyState } from '@/components/EmptyState';
import { formatMoney } from '@/components/Money';
import { OwnerChips } from '@/components/OwnerChips';
import { PageHeader } from '@/components/page/PageHeader';
import { SortableTable } from '@/components/page/SortableTable';
import { amountKey } from '@/components/page/sortKey';
import { SwitchField } from '@/components/page/SwitchField';
import { ProblemMessage } from '@/components/ProblemMessage';
import { buttonVariants } from '@/components/ui/button';
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
  const scope = search.scope === 'me' ? 'me' : 'household';
  const ended = search.ended === 1;
  const answer = useSources(scope, ended);
  // An empty list without the ended sources asks once more with them (archived=true returns open and ended alike),
  // to tell « nothing yet » from « nothing running » (spec §10.4).
  const withEnded = useSources(scope, true, !ended && answer.data?.items.length === 0);
  const setEnded = (checked: boolean) =>
    void navigate({ to: '/income', search: (prev: RootSearch & IncomeSearch) => ({ ...prev, ended: checked ? (1 as const) : undefined }), replace: true });

  // One stable frame whatever the answer: the switch must survive the reload its own change causes (focus).
  return (
    <div className="space-y-4">
      <PageHeader title={t('income.title')} actions={<Link to="/income/new" className={buttonVariants()}>{t('income.add')}</Link>} />
      <SwitchField label={t('income.show_ended')} checked={ended} onCheckedChange={setEnded} />
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
        action={<Link to="/income" search={(prev: RootSearch & IncomeSearch) => ({ ...prev, ended: 1 as const })} className={buttonVariants({ variant: 'outline' })}>{t('income.see_ended')}</Link>} />
    );
  if (empty)
    return (
      <EmptyState title={t('income.empty_title')} body={t('income.empty_body')}
        action={<Link to="/income/new" className={buttonVariants()}>{t('income.add')}</Link>} />
    );

  return (
    <>
      <p className="text-sm text-muted-foreground">{t('income.smoothed')}</p>
      <SortableTable<IncomeSourceSummary> caption={t('income.list')} rows={answer.data.items} rowKey={(r) => r.id}
        rowLink={(r) => ({ to: '/income/$id', params: { id: r.id } })}
        columns={[
          { id: 'name', header: t('lists.col.name'), sort: (r) => r.displayName, cell: (r) => <Name item={r} /> },
          { id: 'kind', header: t('lists.col.kind'), sort: (r) => kindText(t, r.kind), cell: (r) => kindText(t, r.kind) },
          { id: 'payer', header: t('lists.col.payer'), sort: (r) => r.payerLabel ?? '', cell: (r) => r.payerLabel },
          { id: 'owner', header: t('lists.col.owner'), cell: (r) => <OwnerChips owners={r.owners} /> },
          { id: 'month', header: t('lists.col.per_month'), align: 'end', sort: (r) => amountKey(r.monthlyEquivalent.amount),
            cell: (r) => <span className="inline-flex flex-col items-end"><span>{formatMoney(r.monthlyEquivalent)}</span><Gaps item={r} /></span> },
        ]}
        mobile={(r) => (
          <div className="space-y-1">
            <div className="flex items-center justify-between gap-2"><Name item={r} /><OwnerChips owners={r.owners} /></div>
            <div className="flex flex-wrap items-center justify-between gap-2 text-sm">
              <span className="text-muted-foreground">{kindText(t, r.kind)}</span>
              <span className="tabular-nums">{formatMoney(r.monthlyEquivalent)}</span>
            </div>
            <Gaps item={r} />
          </div>
        )} />
    </>
  );
}

function Name({ item }: { item: IncomeSourceSummary }) {
  const { t } = useTranslation();
  return (
    <span className="inline-flex flex-wrap items-center gap-2">
      <span className="font-medium">{item.displayName}</span>
      {item.ended ? <span className="inline-flex items-center gap-1 text-sm text-muted-foreground"><Archive aria-hidden="true" className="size-4" />{t('income.ended_mark')}</span> : null}
      <AccessBadge access={item.visibility === 'private' ? 'private' : 'shared'} />
    </span>
  );
}

function Gaps({ item }: { item: IncomeSourceSummary }) {
  const { t } = useTranslation();
  return item.gapSoon.map((label) => (
    <span key={label} role="note" className="flex items-center gap-1 text-xs font-normal">
      <AlertTriangle aria-hidden="true" className="size-3.5 text-status-warning" />
      {t('income.gap_soon', { label })}
    </span>
  ));
}

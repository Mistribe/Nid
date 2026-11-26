import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import type { ProposalsSearch } from '@/app/router';
import type { RootSearch } from '@/lib/search';
import { EmptyState } from '@/components/EmptyState';
import { DetailTabs } from '@/components/page/DetailTabs';
import { FilterField, FilterRow } from '@/components/page/FilterRow';
import { PageHeader } from '@/components/page/PageHeader';
import { Pager, useListPage, useStepBack } from '@/components/page/Pager';
import { SearchInput } from '@/components/page/SearchInput';
import { SortableTable } from '@/components/page/SortableTable';
import { ProblemMessage } from '@/components/ProblemMessage';
import { formatInstantDay } from '@/lib/format';
import { ToLinkLink } from '../operations/ToLinkLink';
import { PROPOSALS, type ProposalView } from '@/api/proposals';
import { kindOf, originText } from './kinds';
import { isRouteKind, routeSummary, useNames } from './routeKind';
import '@/locales/forms';
import '@/locales/proposals';

const STATUSES = ['pending', 'approved', 'rejected'] as const;

/** Spec 4C2 §3: the proposals by status, one row each with the statement they come from. */
export function ProposalsPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const search: RootSearch & ProposalsSearch = useSearch({ strict: false });
  const navigate = useNavigate();
  const status = search.status ?? 'pending';
  // #774: the search and the page go to the server. The pending ones are few, so read whole (the badge shares that answer);
  // the decided ones are history, read a page at a time.
  const [page, toPage] = useListPage();
  const paged = status !== 'pending';
  const answer = $api.useQuery('get', PROPOSALS, { params: { query: { status, ...(search.target ? { targetItemId: search.target } : {}),
    ...(search.q ? { q: search.q } : {}), ...(paged ? { page } : {}) } } });
  useStepBack(page, answer.data, toPage);
  const names = useNames(answer.data?.items.map((p) => p.kind) ?? []);
  const go = (change: ProposalsSearch) =>
    void navigate({ to: '/proposals', search: ((prev: ProposalsSearch) => ({ ...prev, page: undefined, ...change })) as never, replace: true, resetScroll: false });
  const setStatus = (s: string) => go({ status: s === 'approved' || s === 'rejected' ? s : undefined });

  const summary = (p: ProposalView) =>
    kindOf(p.kind)?.summary(t, p) ?? (isRouteKind(p.kind) ? routeSummary(t, p, names)
      : t('proposals.unknown_summary', { kind: p.kind, target: p.targetName ?? t('proposals.unknown_target') }));

  const list = (
    <div className="space-y-2">
      {search.target ? (
        <p className="text-sm">
          {t('proposal.narrowed', { name: answer.data?.items[0]?.targetName ?? t('proposals.unknown_target') })}
          {' · '}
          <Link to="/proposals" search={search.status ? { status: search.status } : {}} className="underline">{t('proposal.show_all')}</Link>
        </p>
      ) : null}
      <FilterRow label={t('common.filters')}>
        <FilterField label={t('proposal.search')} labelFor className="w-full sm:w-auto">
          {(labelId, controlId) => <SearchInput id={controlId} labelledBy={labelId} value={search.q ?? ''} onSearch={(q) => go({ q: q || undefined })} />}
        </FilterField>
      </FilterRow>
      {answer.error ? <ProblemMessage problem={present(toProblem(answer.error))} /> : null}
      {!answer.data && !answer.error ? <div role="status" aria-busy="true" aria-label={t('proposals.loading')} className="h-24 shimmer rounded-md border bg-muted" /> : null}
      {answer.data && answer.data.total === 0 && search.q ? <p className="text-muted-foreground">{t('proposal.no_match')}</p> : null}
      {answer.data && answer.data.total === 0 && !search.q ? <EmptyState module="/proposals" title={t(`proposals.empty.${status}`)} body={t(`proposals.empty_body.${status}`)} /> : null}
      {answer.data && answer.data.items.length > 0 ? (
        <SortableTable<ProposalView> caption={t('proposals.list')} rows={answer.data.items} rowKey={(p) => p.id}
          initialSort={{ id: 'received', desc: true }}
          columns={[
            { id: 'document', header: t('lists.col.document'), wrap: true, sort: (p) => p.sourceLabel ?? '', cell: (p) => p.sourceLabel ?? <span className="text-muted-foreground">{t('proposals.no_source')}</span> },
            { id: 'item', header: t('lists.col.item'), wrap: true, sort: (p) => summary(p), cell: (p) => <Link to="/proposals/$id" params={{ id: p.id }} className="underline">{summary(p)}</Link> },
            { id: 'received', header: t('lists.col.received'), sort: (p) => p.createdAt,
              cell: (p) => <span className="inline-flex flex-col"><span>{formatInstantDay(p.createdAt)}</span><span className="text-xs text-muted-foreground">{originText(t, p.createdVia)}</span></span> },
            { id: 'state', header: t('lists.col.state'), sort: (p) => p.status, cell: (p) => <State p={p} /> },
          ]}
          mobile={(p) => (
            <div className="space-y-1">
              <Link to="/proposals/$id" params={{ id: p.id }} className="underline">{summary(p)}</Link>
              <p className="text-sm text-muted-foreground">{p.sourceLabel ?? t('proposals.no_source')} <Sep /> {formatInstantDay(p.createdAt)} <Sep /> <State p={p} /></p>
            </div>
          )} />
      ) : null}
      {paged && answer.data ? <Pager page={page} total={answer.data.total} label={t('proposal.pages')} onPage={toPage} /> : null}
    </div>
  );

  return (
    <div className="space-y-4">
      <PageHeader title={t('proposals.title')} actions={<ToLinkLink />} />
      {/* The status as tabs, like every list's (one list, read for the open tab's status). */}
      <DetailTabs value={status} onChange={setStatus}
        tabs={STATUSES.map((s) => ({ id: s, label: t(`proposals.tab.${s}`), content: list }))} />
    </div>
  );
}

function State({ p }: { p: ProposalView }) {
  const { t } = useTranslation();
  return p.status === 'pending'
    ? t('proposals.tab.pending')
    : t(`proposals.decided.${p.status === 'rejected' ? 'rejected' : 'approved'}`, { date: formatInstantDay(p.decidedAt ?? p.createdAt) });
}

function Sep() {
  const { t } = useTranslation();
  return <span aria-hidden="true">{t('lists.sep')}</span>;
}

import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import type { ProposalsSearch, RootSearch } from '@/app/router';
import { EmptyState } from '@/components/EmptyState';
import { DetailTabs } from '@/components/page/DetailTabs';
import { PageHeader } from '@/components/page/PageHeader';
import { SortableTable } from '@/components/page/SortableTable';
import { ProblemMessage } from '@/components/ProblemMessage';
import { formatInstantDay } from '@/features/admin/format';
import { ToLinkLink } from '../operations/ToLinkLink';
import { PROPOSALS, type ProposalView } from './api';
import { kindOf, originText } from './kinds';
import { isRouteKind, routeSummary, useNames } from './routeKind';
import '@/locales/forms';

const STATUSES = ['pending', 'approved', 'rejected'] as const;

/** Spec 4C2 §3: the proposals by status, one row each with the statement they come from. */
export function ProposalsPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const search: RootSearch & ProposalsSearch = useSearch({ strict: false });
  const navigate = useNavigate();
  const status = search.status ?? 'pending';
  const answer = $api.useQuery('get', PROPOSALS, { params: { query: { status } } });
  const names = useNames(answer.data?.items.map((p) => p.kind) ?? []);
  const setStatus = (s: string) =>
    void navigate({ to: '/proposals', search: (prev) => ({ ...prev, status: s === 'approved' || s === 'rejected' ? s : undefined }), replace: true, resetScroll: false });

  const summary = (p: ProposalView) =>
    kindOf(p.kind)?.summary(t, p) ?? (isRouteKind(p.kind) ? routeSummary(t, p, names)
      : t('proposals.unknown_summary', { kind: p.kind, target: p.targetName ?? t('proposals.unknown_target') }));

  const list = (
    <div>
      {answer.error ? <ProblemMessage problem={present(toProblem(answer.error))} /> : null}
      {!answer.data && !answer.error ? <div role="status" aria-busy="true" aria-label={t('proposals.loading')} className="h-24 shimmer rounded-md border bg-muted" /> : null}
      {answer.data && answer.data.items.length === 0 ? <EmptyState module="/proposals" title={t(`proposals.empty.${status}`)} body={t(`proposals.empty_body.${status}`)} /> : null}
      {answer.data && answer.data.items.length > 0 ? (
        <SortableTable<ProposalView> caption={t('proposals.list')} rows={answer.data.items} rowKey={(p) => p.id}
          initialSort={{ id: 'received', desc: true }}
          columns={[
            { id: 'document', header: t('lists.col.document'), sort: (p) => p.sourceLabel ?? '', cell: (p) => p.sourceLabel ?? <span className="text-muted-foreground">{t('proposals.no_source')}</span> },
            { id: 'item', header: t('lists.col.item'), sort: (p) => summary(p), cell: (p) => <Link to="/proposals/$id" params={{ id: p.id }} className="underline">{summary(p)}</Link> },
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

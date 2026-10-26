import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import { lazy, Suspense, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { present, toProblem } from '@/api/errors';
import type { RootSearch } from '@/app/router';
import { EmptyState } from '@/components/EmptyState';
import { LocalDate } from '@/components/LocalDate';
import { DetailTabs, useDetailTab } from '@/components/page/DetailTabs';
import { PageHeader } from '@/components/page/PageHeader';
import { RowMenu } from '@/components/page/RowMenu';
import { SortableTable } from '@/components/page/SortableTable';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Badge } from '@/components/ui/badge';
import { contentUrl, useArchiveDocument, useDocuments, useUnarchiveDocument, type DocumentSummary, type Scope } from './api';
import { applyFilters, DocumentFilters, keepOffered, NO_FILTER } from './DocumentFilters';
import { UploadList, UploadPicker, UploadZone, useUploads } from './DocumentUpload';
import { fileSize, roleText, shownDate } from './format';
import '@/locales/documents';

const ConfirmDialog = lazy(() => import('@/components/ConfirmDialog'));

const TABS = ['all', 'inbox'] as const;

/** Design §2.2: every document, or the unlinked ones waiting in « À classer »; files dropped here land there. */
export function DocumentsPage() {
  const { t } = useTranslation();
  const { scope }: RootSearch = useSearch({ strict: false });
  const [tab, setTab] = useDetailTab(TABS);
  // Held by the page: the switch keeps its state across the tabs.
  const [archived, setArchived] = useState(false);
  const { uploads, send } = useUploads();
  const scoped = scope === 'me' ? 'me' : 'household';
  const list = (inbox: boolean) => <List scope={scoped} inbox={inbox} archived={archived} onArchivedChange={setArchived} />;
  return (
    <div className="space-y-4">
      <PageHeader title={t('documents_page.title')} actions={<UploadPicker onFiles={send} />} />
      <UploadZone onFiles={send} />
      <UploadList uploads={uploads} />
      <DetailTabs value={tab} onChange={setTab} tabs={[
        { id: 'all', label: t('documents_page.tab_all'), content: list(false) },
        { id: 'inbox', label: t('documents_page.tab_inbox'), content: list(true) },
      ]} />
    </div>
  );
}

function List({ scope, inbox, archived, onArchivedChange }: { scope: Scope; inbox: boolean; archived: boolean; onArchivedChange: (on: boolean) => void }) {
  const { t } = useTranslation();
  const answer = useDocuments(scope, archived, inbox);
  const [filters, setFilters] = useState(NO_FILTER);
  const all = answer.data?.items ?? [];
  // A selection the new rows no longer offer is dropped, rather than hiding every row behind an option that is gone.
  const kept = keepOffered(filters, all);
  if (answer.data && kept !== filters) setFilters(kept);
  // One stable frame whatever the answer: the switch must survive the reload its own change causes (focus).
  return (
    <>
      <DocumentFilters rows={all} filters={filters} onChange={setFilters} archived={archived} onArchivedChange={onArchivedChange} />
      {answer.error ? <ProblemMessage problem={present(toProblem(answer.error))} />
        : !answer.data ? (
          <ul aria-busy="true" aria-label={t('documents_page.loading')} className="space-y-2">
            {[0, 1, 2].map((i) => <li key={i} className="h-16 shimmer rounded-md border bg-muted" />)}
          </ul>
        )
        : all.length === 0 ? (inbox
          ? <EmptyState title={t('documents_page.empty_inbox_title')} body={t('documents_page.empty_inbox_body')} />
          : <EmptyState title={t('documents_page.empty_title')} body={t('documents_page.empty_body')} />)
        : <Table rows={applyFilters(all, filters)} />}
    </>
  );
}

function Table({ rows }: { rows: DocumentSummary[] }) {
  const { t } = useTranslation();
  if (rows.length === 0) return <p className="text-muted-foreground">{t('documents_page.no_match')}</p>;
  const types = (d: DocumentSummary) => [...new Set(d.links.map((l) => roleText(t, l.role)))].join(', ');
  const linked = (d: DocumentSummary) => [...new Set(d.links.map((l) => l.label))].join(', ');
  const date = (d: DocumentSummary) => <LocalDate value={shownDate(d)} style="short" />;
  return (
    <SortableTable<DocumentSummary> caption={t('documents_page.list')} rows={rows} rowKey={(d) => d.id}
      columns={[
        { id: 'title', header: t('documents_page.col_title'), sort: (d) => d.title, cell: (d) => <Title doc={d} /> },
        { id: 'types', header: t('documents_page.col_types'), sort: types, cell: types },
        { id: 'links', header: t('documents_page.col_links'), sort: linked, cell: linked },
        { id: 'date', header: t('documents_page.col_date'), sort: shownDate, cell: date },
        { id: 'size', header: t('documents_page.col_size'), align: 'end', sort: (d) => d.sizeBytes, cell: (d) => fileSize(t, d.sizeBytes) },
        { id: 'menu', header: t('documents_page.col_actions'), align: 'end', cell: (d) => <Actions doc={d} /> },
      ]}
      mobile={(d) => (
        <div className="space-y-1">
          <div className="flex items-center justify-between gap-2"><Title doc={d} /><Actions doc={d} /></div>
          <div className="text-sm text-muted-foreground">{[types(d), linked(d)].filter(Boolean).join(' · ')}</div>
          <div className="flex justify-between gap-2 text-sm text-muted-foreground">{date(d)}<span>{fileSize(t, d.sizeBytes)}</span></div>
        </div>
      )} />
  );
}

function Title({ doc }: { doc: DocumentSummary }) {
  const { t } = useTranslation();
  return (
    <span className="inline-flex items-center gap-2">
      <Link to="/documents/$id" params={{ id: doc.id }} className="font-medium underline-offset-4 hover:underline">{doc.title}</Link>
      {doc.archived ? <Badge variant="secondary">{t('documents_page.archived')}</Badge> : null}
    </span>
  );
}

/** The row is gone once the list reloads: the focus goes to the tab list, which stays. */
const focusTabs = () => document.querySelector<HTMLElement>('[role="tab"][data-state="active"]')?.focus();

function Actions({ doc }: { doc: DocumentSummary }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const [asking, setAsking] = useState(false);
  const archive = useArchiveDocument();
  const unarchive = useUnarchiveDocument();
  const error = archive.error ?? unarchive.error;
  // A second click while a write is in flight would send it twice.
  const pending = archive.isPending || unarchive.isPending;
  const path = { params: { path: { id: doc.id } } };
  const writes = doc.canEdit ? [
    // The edit dialog lives on the detail page.
    { label: t('documents_page.edit'), onSelect: () => void navigate({ to: '/documents/$id', params: { id: doc.id } }) },
    doc.archived
      ? { label: t('documents_page.unarchive'), onSelect: () => { if (!pending) unarchive.mutate(path); } }
      : { label: t('documents_page.archive'), onSelect: () => { if (!pending) setAsking(true); } },
  ] : [];
  return (
    <span className="flex flex-wrap items-center justify-end gap-2">
      <RowMenu label={t('documents_page.row_menu', { title: doc.title })}
        items={[{ label: t('documents_page.download'), href: contentUrl(doc.id) }, ...writes]} />
      {asking ? (
        <Suspense fallback={null}>
          <ConfirmDialog label={t('documents_page.archive')} confirmation={t('documents_page.archive_confirmation', { title: doc.title })} pending={archive.isPending}
            onClose={() => setAsking(false)} onConfirm={() => { setAsking(false); archive.mutate(path, { onSuccess: focusTabs }); }} />
        </Suspense>
      ) : null}
      {error ? <ProblemMessage problem={present(toProblem(error))} /> : null}
    </span>
  );
}

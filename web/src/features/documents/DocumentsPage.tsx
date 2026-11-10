import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import { lazy, Suspense, useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { present, toProblem } from '@/api/errors';
import type { DocumentsSearch } from '@/app/router';
import type { RootSearch } from '@/lib/search';
import { EmptyState } from '@/components/EmptyState';
import { LocalDate } from '@/components/LocalDate';
import { OwnerChips } from '@/components/OwnerChips';
import { DetailTabs, useDetailTab } from '@/components/page/DetailTabs';
import { Pager } from '@/components/page/Pager';
import { PageHeader } from '@/components/page/PageHeader';
import { RowMenu } from '@/components/page/RowMenu';
import { SortableTable, type Selection } from '@/components/page/SortableTable';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Tooltip, TooltipContent, TooltipProvider, TooltipTrigger } from '@/components/ui/tooltip';
import { contentUrl, type DocumentSummary } from '@/api/documents';
import { PAGE_SIZE, useArchiveDocument, useDocuments, useUnarchiveDocument, type Scope } from './api';
import { previewable, usePreview } from './preview';
import { ExpectedDocuments } from './ExpectedDocuments';
import { DocumentFilters, keepOffered, type Filters } from './DocumentFilters';
import { FileDocumentsDialog, type Filed } from './FileDocumentsDialog';
import { UploadList, UploadPicker, UploadZone, useUploads } from './DocumentUpload';
import { fileSize, roleText, shownDate } from './format';
import '@/locales/documents';
import { Loading, TableSkeleton } from '@/components/page/skeletons';

const ConfirmDialog = lazy(() => import('@/components/ConfirmDialog'));

const TABS = ['all', 'inbox'] as const;

/** Design §2.2: every document, or the unlinked ones waiting in « À classer »; files dropped here land there. */
export function DocumentsPage() {
  const { t } = useTranslation();
  const { scope }: RootSearch = useSearch({ strict: false });
  const [tab, setTab] = useDetailTab(TABS);
  // Held by the page: the switch keeps its state across the tabs.
  const [archived, setArchived] = useState(false);
  const { uploads, send, refuse, keep, addAnyway } = useUploads();
  const scoped = scope === 'me' ? 'me' : 'household';
  const list = (inbox: boolean) => <List scope={scoped} inbox={inbox} archived={archived} onArchivedChange={setArchived} />;
  return (
    <div className="space-y-4">
      <PageHeader title={t('documents_page.title')} actions={<UploadPicker onFiles={send} />} />
      <UploadZone onFiles={send} onRefused={refuse} />
      <UploadList uploads={uploads} onKeep={keep} onAddAnyway={addAnyway} />
      <ExpectedDocuments />
      <DetailTabs value={tab} onChange={setTab} tabs={[
        { id: 'all', label: t('documents_page.tab_all'), content: list(false) },
        { id: 'inbox', label: t('documents_page.tab_inbox'), content: list(true) },
      ]} />
    </div>
  );
}

function List({ scope, inbox, archived, onArchivedChange }: { scope: Scope; inbox: boolean; archived: boolean; onArchivedChange: (on: boolean) => void }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  // Issue #420: the filters and the page live in the URL, and the server applies them.
  const search: DocumentsSearch = useSearch({ strict: false });
  const page = search.page ?? 1;
  const filters: Filters = { type: search.type ?? '', year: search.year ? String(search.year) : '', item: search.item ?? '', q: search.q ?? '' };
  const answer = useDocuments(scope, archived, inbox, { role: search.type, year: search.year, linked: search.item, q: search.q, page });
  // « À classer » only (issue #183): the documents to file together, and those the last « Classer » refused, with why.
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set());
  const [refused, setRefused] = useState<Filed['refused']>([]);
  const [filing, setFiling] = useState(false);
  const fileRef = useRef<HTMLButtonElement>(null);
  const shown = answer.data?.items ?? [];
  const total = answer.data?.total ?? 0;
  const go = (change: Partial<DocumentsSearch>) =>
    void navigate({ to: '.', replace: true, resetScroll: false, search: ((prev: DocumentsSearch) => ({ ...prev, ...change })) as never });
  const filter = (next: Filters) => go({ type: next.type || undefined, year: next.year ? Number(next.year) : undefined,
    item: next.item || undefined, q: next.q || undefined, page: undefined });
  const toPage = (n: number) => go({ page: n > 1 ? n : undefined });
  // A new page, a filter or the archived switch starts a new selection: what was ticked may no longer be shown.
  const asked = JSON.stringify([archived, filters, page]);
  const [shownAsked, setShownAsked] = useState(asked);
  if (shownAsked !== asked) { setShownAsked(asked); setSelected(new Set()); setRefused([]); }
  // A filter the list no longer offers is dropped rather than hiding every row; a page past the end (the last rows filed) steps back.
  const settled = answer.data && !answer.isPlaceholderData ? answer.data : undefined;
  useEffect(() => {
    if (!settled) return;
    const kept = keepOffered(filters, settled.facets);
    if (kept !== filters) filter(kept);
    else if (settled.items.length === 0 && page > 1) toPage(Math.ceil(settled.total / PAGE_SIZE));
  });
  // A filed document leaves the list: what is still selected is what is still shown.
  const chosen = inbox ? shown.filter((d) => selected.has(d.id)) : [];
  const filed = (message: string, outcome: Filed) => {
    setFiling(false);
    toast.success(message);
    setSelected(new Set(outcome.refused.map((r) => r.id)));
    setRefused(outcome.refused);
    if (outcome.refused.length > 0) fileRef.current?.focus(); else focusTabs();
  };
  const narrowed = Object.values(filters).some(Boolean);
  // One stable frame whatever the answer: the switch must survive the reload its own change causes (focus).
  return (
    <>
      <DocumentFilters facets={answer.data?.facets} filters={filters} onChange={filter} archived={archived} onArchivedChange={onArchivedChange} />
      {chosen.length > 0 ? (
        <div className="flex flex-wrap items-center gap-3 rounded-md border bg-muted/50 px-3 py-2">
          <span className="text-sm">{t('documents_page.file.selected', { count: chosen.length })}</span>
          <Button ref={fileRef} type="button" size="sm" onClick={() => setFiling(true)}>{t('documents_page.file.open')}</Button>
        </div>
      ) : null}
      {refused.length > 0 ? (
        <ul aria-label={t('documents_page.file.refused')} className="space-y-2">
          {refused.map((r) => (
            <li key={r.id} className="space-y-1">
              <p className="text-sm font-medium">{shown.find((d) => d.id === r.id)?.title}</p>
              <ProblemMessage problem={present(toProblem(r.problem))} />
            </li>
          ))}
        </ul>
      ) : null}
      {filing ? <FileDocumentsDialog docs={chosen} onClose={() => setFiling(false)} onDone={filed} /> : null}
      {answer.error ? <ProblemMessage problem={present(toProblem(answer.error))} />
        : !answer.data ? (
          <Loading label={t('documents_page.loading')}><TableSkeleton columns={7} /></Loading>
        )
        : total === 0 && !narrowed ? (inbox
          ? <EmptyState module="/documents" title={t('documents_page.empty_inbox_title')} body={t('documents_page.empty_inbox_body')} />
          : <EmptyState module="/documents" title={t('documents_page.empty_title')} body={t('documents_page.empty_body')} />)
        : (
          <>
            <p className="text-sm text-muted-foreground">{t('documents_page.count', { count: total })}</p>
            <Table rows={shown} selection={inbox ? { selected, onChange: setSelected, allLabel: t('documents_page.file.select_all'),
              label: (d) => t('documents_page.file.select', { title: d.title }), disabled: (d) => !d.canEdit } : undefined} />
            <Pager page={page} total={total} label={t('documents_page.pages')} onPage={toPage} />
          </>
        )}
    </>
  );
}

function Table({ rows, selection }: { rows: DocumentSummary[]; selection?: Selection<DocumentSummary> }) {
  const { t } = useTranslation();
  if (rows.length === 0) return <p className="text-muted-foreground">{t('documents_page.no_match')}</p>;
  const types = (d: DocumentSummary) => [...new Set(d.links.map((l) => roleText(t, l.role)))].join(', ');
  const labels = (d: DocumentSummary) => [...new Set(d.links.map((l) => l.label))];
  const linked = (d: DocumentSummary) => labels(d).join(', ');
  const date = (d: DocumentSummary) => <LocalDate value={shownDate(d)} style="short" />;
  // The uploader: the person a document belongs to (private to them while it has no link).
  const owner = (d: DocumentSummary) => <OwnerChips owners={[{ personId: d.uploadedBy, shareBp: 10_000 }]} />;
  return (
    <TooltipProvider>
    <SortableTable<DocumentSummary> caption={t('documents_page.list')} rows={rows} rowKey={(d) => d.id} selection={selection}
      columns={[
        { id: 'title', header: t('documents_page.col_title'), sort: (d) => d.title, cell: (d) => <Title doc={d} /> },
        { id: 'owner', header: t('documents_page.col_owner'), cell: owner },
        { id: 'types', header: t('documents_page.col_types'), sort: types, cell: types },
        { id: 'links', header: t('documents_page.col_links'), sort: linked, cell: (d) => <Linked labels={labels(d)} /> },
        { id: 'date', header: t('documents_page.col_date'), sort: shownDate, cell: date },
        { id: 'size', header: t('documents_page.col_size'), align: 'end', sort: (d) => d.sizeBytes, cell: (d) => fileSize(t, d.sizeBytes) },
        { id: 'menu', header: t('documents_page.col_actions'), align: 'end', cell: (d) => <Actions doc={d} /> },
      ]}
      mobile={(d) => (
        <div className="space-y-1">
          <div className="flex items-center justify-between gap-2"><Title doc={d} /><Actions doc={d} /></div>
          <div className="text-sm text-muted-foreground">{[types(d), linked(d)].filter(Boolean).join(' · ')}</div>
          <div className="flex justify-between gap-2 text-sm text-muted-foreground">{date(d)}{owner(d)}<span>{fileSize(t, d.sizeBytes)}</span></div>
        </div>
      )} />
    </TooltipProvider>
  );
}

/** « Liés à » cut with an ellipsis, the whole list in a tooltip (issue 392); the full text stays in the cell for a screen reader. */
function Linked({ labels }: { labels: string[] }) {
  if (labels.length === 0) return null;
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <span tabIndex={0} className="block max-w-64 truncate">{labels.join(', ')}</span>
      </TooltipTrigger>
      <TooltipContent><ul>{labels.map((label) => <li key={label}>{label}</li>)}</ul></TooltipContent>
    </Tooltip>
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
  const preview = usePreview();
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
        items={[...(previewable(doc.mediaType) ? [{ label: t('documents_page.preview'), onSelect: () => preview.show(doc) }] : []),
          { label: t('documents_page.download'), href: contentUrl(doc.id) }, ...writes]} />
      {preview.dialog}
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

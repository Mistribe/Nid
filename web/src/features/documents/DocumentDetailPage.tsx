import { Navigate, useParams } from '@tanstack/react-router';
import { Download, Plus } from 'lucide-react';
import { useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { present, toProblem } from '@/api/errors';
import { ConfirmFromMenu } from '@/components/ConfirmAction';
import { LocalDate } from '@/components/LocalDate';
import { PageHeader } from '@/components/page/PageHeader';
import { PageTrail } from '@/components/page/trail';
import { RowMenu, shownOr } from '@/components/page/RowMenu';
import { Section } from '@/components/page/Section';
import { StatLine } from '@/components/page/StatLine';
import { ErrorPage } from '@/components/ErrorPage';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { AddLinkDialog } from './AddLinkDialog';
import { contentUrl } from '@/api/documents';
import { useArchiveDocument, useDocument, useDocumentTargets, useUnarchiveDocument } from './api';
import { DocumentLinks } from './DocumentLinks';
import { EditDocumentDialog } from './EditDocumentDialog';
import { fileSize, localDay } from './format';
import { DocumentViewer } from './DocumentViewer';
import { previewable } from './preview';
import '@/locales/documents';
import { PageSkeleton } from '@/components/page/skeletons';

/**
 * Design §2.2: the title with « Télécharger » and « Modifier », « Archiver » in its `⋯` menu, the file's figures inline, then
 * the elements it is linked to. The uploader's name comes from the persons the actor may link to; one they cannot see stays unnamed.
 */
export function DocumentDetailPage() {
  const { t } = useTranslation();
  const { id } = useParams({ from: '/documents/$id' });
  const answer = useDocument(id);
  const targets = useDocumentTargets();
  const archive = useArchiveDocument();
  const unarchive = useUnarchiveDocument();
  const [archiving, setArchiving] = useState(false);
  const [dialog, setDialog] = useState<'editing' | 'linking' | null>(null);
  const editRef = useRef<HTMLButtonElement>(null);
  const addRef = useRef<HTMLButtonElement>(null);
  const moreRef = useRef<HTMLButtonElement>(null);
  // No `PageHeader` yet: the bar's trail leads back to the list (no inline link of its own).
  const crumbs = [{ label: t('documents_page.title'), to: '/documents' as const }];
  const trail = <PageTrail crumbs={crumbs} />;

  if (!answer.data && answer.error) return <ErrorPage problem={present(toProblem(answer.error))} crumbs={crumbs} onRetry={() => void answer.refetch()} />;
  if (!answer.data) return <>{trail}<PageSkeleton label={t('documents_page.detail.loading')} actions={2} stats={3} /></>;

  // A refetch that failed under data on screen: a 404 means the actor no longer reads the document (its last link was theirs).
  if (answer.error && toProblem(answer.error).status === 404) return <Navigate to="/documents" replace />;
  const detail = answer.data;
  const doc = detail.document;
  const path = { params: { path: { id: doc.id } } };
  const uploader = targets.data?.items.find((x) => x.kind === 'core.person' && x.id === detail.uploadedBy)?.label;
  // The dialog handed itself back: the page says what happened and puts the focus back on the button that opened it.
  const done = (ref: typeof editRef) => (message: string) => { setDialog(null); toast.success(message); shownOr(ref.current, moreRef.current)?.focus(); };
  const error = archive.error ?? unarchive.error ?? answer.error;

  const actions = (
    <>
      <Button asChild variant="outline"><a href={contentUrl(doc.id)} download><Download aria-hidden="true" />{t('documents_page.download')}</a></Button>
      {doc.canEdit ? (
        <>
          <Button ref={editRef} type="button" variant="outline" className="max-sm:hidden" onClick={() => setDialog('editing')}>{t('documents_page.edit')}</Button>
          <RowMenu label={t('page.more_actions')} triggerRef={moreRef} items={[{ label: t('documents_page.edit'), phone: true, onSelect: () => setDialog('editing') }, doc.archived
            ? { label: t('documents_page.unarchive'), onSelect: () => { if (!unarchive.isPending) unarchive.mutate(path, { onSuccess: () => { toast.success(t('documents_page.detail.status_unarchived')); shownOr(editRef.current, moreRef.current)?.focus(); } }); } }
            : { label: t('documents_page.archive'), disabled: archive.isPending, onSelect: () => setArchiving(true) }]} />
        </>
      ) : null}
    </>
  );

  return (
    <article className="space-y-6">
      <PageHeader crumbs={crumbs} title={doc.title} actions={actions}
        meta={doc.archived ? <Badge variant="secondary">{t('documents_page.archived')}</Badge> : null}>
        <section aria-label={t('documents_page.detail.figures')}>
          <StatLine items={[
            { label: t('documents_page.detail.file_type'), value: doc.mediaType.split('/')[1]?.toUpperCase() ?? doc.mediaType },
            { label: t('documents_page.detail.size'), value: fileSize(t, doc.sizeBytes) },
            { label: t('documents_page.detail.uploaded_on'), value: <LocalDate value={localDay(doc.uploadedAt)} /> },
            ...(uploader ? [{ label: t('documents_page.detail.uploaded_by'), value: uploader }] : []),
          ]} />
        </section>
      </PageHeader>
      {error ? <ProblemMessage problem={present(toProblem(error))} /> : null}
      {detail.note ? (
        <Section title={t('documents_page.detail.note')}><p className="whitespace-pre-wrap">{detail.note}</p></Section>
      ) : null}
      <Section title={t('documents_page.links.title')}>
        <DocumentLinks doc={doc} onRemoved={() => addRef.current?.focus()} />
        {doc.canEdit ? (
          <Button ref={addRef} type="button" variant="outline" onClick={() => setDialog('linking')}>
            <Plus aria-hidden="true" />{t('documents_page.links.add')}
          </Button>
        ) : null}
      </Section>
      {/* Issue #413: a PDF or an image is shown in the page, under its links, after the header's « Télécharger » in the tab order. */}
      {previewable(doc.mediaType) ? <Section title={t('documents_page.detail.preview')}><DocumentViewer doc={doc} /></Section> : null}
      {archiving ? (
        <ConfirmFromMenu label={t('documents_page.archive')} confirmation={t('documents_page.archive_confirmation', { title: doc.title })} pending={archive.isPending}
          returnFocus={moreRef} onClose={() => setArchiving(false)}
          onConfirm={() => archive.mutate(path, { onSuccess: () => { toast.success(t('documents_page.detail.status_archived')); shownOr(editRef.current, moreRef.current)?.focus(); } })} />
      ) : null}
      {dialog === 'editing' ? <EditDocumentDialog detail={detail} onClose={() => setDialog(null)} onDone={done(editRef)} /> : null}
      {dialog === 'linking' ? <AddLinkDialog doc={doc} onClose={() => setDialog(null)} onDone={done(addRef)} /> : null}
    </article>
  );
}

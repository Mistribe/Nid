import { Link, Navigate, useParams } from '@tanstack/react-router';
import { Download, Plus } from 'lucide-react';
import { useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { present, toProblem } from '@/api/errors';
import { ConfirmAction } from '@/components/ConfirmAction';
import { LocalDate } from '@/components/LocalDate';
import { PageHeader } from '@/components/page/PageHeader';
import { Section } from '@/components/page/Section';
import { StatLine } from '@/components/page/StatLine';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { AddLinkDialog } from './AddLinkDialog';
import { contentUrl, useArchiveDocument, useDocument, useDocumentTargets, useUnarchiveDocument } from './api';
import { DocumentLinks } from './DocumentLinks';
import { EditDocumentDialog } from './EditDocumentDialog';
import { fileSize, localDay } from './format';
import '@/locales/documents';

/**
 * Design §2.2: the title with « Télécharger », « Modifier » and « Archiver », the file's figures inline, then the elements it
 * is linked to. The uploader's name comes from the persons the actor may link to; one they cannot see stays unnamed.
 */
export function DocumentDetailPage() {
  const { t } = useTranslation();
  const { id } = useParams({ from: '/documents/$id' });
  const answer = useDocument(id);
  const targets = useDocumentTargets();
  const archive = useArchiveDocument();
  const unarchive = useUnarchiveDocument();
  const [dialog, setDialog] = useState<'editing' | 'linking' | null>(null);
  const [status, setStatus] = useState('');
  const editRef = useRef<HTMLButtonElement>(null);
  const addRef = useRef<HTMLButtonElement>(null);
  const back = <Link to="/documents" className="text-sm underline">{t('documents_page.detail.back')}</Link>;

  if (!answer.data && answer.error) return <>{back}<ProblemMessage problem={present(toProblem(answer.error))} /></>;
  if (!answer.data) return <>{back}<div aria-busy="true" aria-label={t('documents_page.detail.loading')} className="h-40 shimmer rounded-md border bg-muted" /></>;

  // A refetch that failed under data on screen: a 404 means the actor no longer reads the document (its last link was theirs).
  if (answer.error && toProblem(answer.error).status === 404) return <Navigate to="/documents" replace />;
  const detail = answer.data;
  const doc = detail.document;
  const path = { params: { path: { id: doc.id } } };
  const uploader = targets.data?.items.find((x) => x.kind === 'core.person' && x.id === detail.uploadedBy)?.label;
  // The dialog handed itself back: the page says what happened and puts the focus back on the button that opened it.
  const done = (ref: typeof editRef) => (message: string) => { setDialog(null); setStatus(message); ref.current?.focus(); };
  const error = archive.error ?? unarchive.error ?? answer.error;

  const actions = (
    <>
      <Button asChild variant="outline"><a href={contentUrl(doc.id)} download><Download aria-hidden="true" />{t('documents_page.download')}</a></Button>
      {doc.canEdit ? (
        <>
          <Button ref={editRef} type="button" variant="outline" onClick={() => { setStatus(''); setDialog('editing'); }}>{t('documents_page.edit')}</Button>
          {doc.archived
            ? <Button type="button" variant="outline" disabled={unarchive.isPending} onClick={() => unarchive.mutate(path, { onSuccess: () => { setStatus(t('documents_page.detail.status_unarchived')); editRef.current?.focus(); } })}>{t('documents_page.unarchive')}</Button>
            : <ConfirmAction label={t('documents_page.archive')} confirmation={t('documents_page.archive_confirmation', { title: doc.title })} pending={archive.isPending}
                onConfirm={() => archive.mutate(path, { onSuccess: () => { setStatus(t('documents_page.detail.status_archived')); editRef.current?.focus(); } })} />}
        </>
      ) : null}
    </>
  );

  return (
    <article className="space-y-6">
      <PageHeader crumbs={[{ label: t('documents_page.title'), to: '/documents' }]} title={doc.title} actions={actions}
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
      <p role="status" aria-live="polite">{status}</p>
      {error ? <ProblemMessage problem={present(toProblem(error))} /> : null}
      {detail.note ? (
        <Section title={t('documents_page.detail.note')}><p className="whitespace-pre-wrap">{detail.note}</p></Section>
      ) : null}
      <Section title={t('documents_page.links.title')}>
        <DocumentLinks doc={doc} onRemoved={() => addRef.current?.focus()} />
        {doc.canEdit ? (
          <Button ref={addRef} type="button" variant="outline" onClick={() => { setStatus(''); setDialog('linking'); }}>
            <Plus aria-hidden="true" />{t('documents_page.links.add')}
          </Button>
        ) : null}
      </Section>
      {dialog === 'editing' ? <EditDocumentDialog detail={detail} onClose={() => setDialog(null)} onDone={done(editRef)} /> : null}
      {dialog === 'linking' ? <AddLinkDialog doc={doc} onClose={() => setDialog(null)} onDone={done(addRef)} /> : null}
    </article>
  );
}

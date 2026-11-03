import { Link, useNavigate } from '@tanstack/react-router';
import { Link2, Upload } from 'lucide-react';
import { lazy, Suspense, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { present, toProblem } from '@/api/errors';
import { LocalDate } from '@/components/LocalDate';
import { RowMenu } from '@/components/page/RowMenu';
import { SortableTable } from '@/components/page/SortableTable';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { contentUrl, useAnchoredDocuments, useUnlinkDocument, type DocumentLink, type DocumentSummary } from './api';
import { linkKey, roleText, shownDate } from './format';
import { ItemLinkDialog } from './ItemLinkDialog';
import { ItemUploadDialog } from './ItemUploadDialog';
import '@/locales/documents';

const ConfirmDialog = lazy(() => import('@/components/ConfirmDialog'));

type Row = { doc: DocumentSummary; link: DocumentLink };

/**
 * The documents anchored on an item, one row per link on it: the item itself (« Cette fiche ») or a row of a module
 * anchored on it (a phase, a one-off), named by the server. « Déposer » and « Relier un document » for an actor who
 * writes the item; « Retirer » unlinks that row's link only. The page's tab or `Section` gives the title.
 */
export function ItemDocuments({ itemId, canWrite, defaultRole }: { itemId: string; canWrite: boolean; defaultRole: string }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const answer = useAnchoredDocuments(itemId);
  const unlink = useUnlinkDocument();
  const [dialog, setDialog] = useState<'upload' | 'link' | null>(null);
  const [removing, setRemoving] = useState<Row | null>(null);
  const uploadRef = useRef<HTMLButtonElement>(null);
  const linkRef = useRef<HTMLButtonElement>(null);
  const done = (ref: typeof uploadRef) => (message: string) => { setDialog(null); toast.success(message); ref.current?.focus(); };

  // A document listed here may also be linked elsewhere (another item, its phase, the household): those links are not this tab's.
  const rows: Row[] = (answer.data?.items ?? []).flatMap((doc) => doc.links
    .filter((l) => l.anchorItemId === itemId).map((link) => ({ doc, link })));
  const linkedTo = ({ link }: Row) => (link.targetKind === 'core.item' ? t('documents_item.this_item') : link.label);
  const role = ({ link }: Row) => roleText(t, link.role);
  const date = ({ doc }: Row) => <LocalDate value={shownDate(doc)} style="short" />;
  const title = ({ doc }: Row) => <Link to="/documents/$id" params={{ id: doc.id }} className="font-medium underline-offset-4 hover:underline">{doc.title}</Link>;
  const menu = (row: Row) => (
    <RowMenu label={t('documents_item.menu', { title: row.doc.title })} items={[
      { label: t('documents_item.download'), href: contentUrl(row.doc.id) },
      { label: t('documents_item.open'), onSelect: () => void navigate({ to: '/documents/$id', params: { id: row.doc.id } }) },
      ...(row.doc.canEdit && row.link.canWrite ? [{ label: t('documents_item.remove'), destructive: true, onSelect: () => setRemoving(row) }] : []),
    ]} />
  );
  const remove = ({ doc, link }: Row) => unlink.mutate({ params: { path: { id: doc.id, targetKind: link.targetKind, targetId: link.targetId } } }, {
    // The row is gone: the focus goes back to the first action, when the actor has it.
    onSuccess: () => { toast.success(t('documents_item.removed')); uploadRef.current?.focus(); },
  });
  const error = unlink.error ?? answer.error;

  return (
    <div className="space-y-3">
      {canWrite ? (
        <div className="flex flex-wrap justify-end gap-2">
          <Button ref={uploadRef} type="button" variant="outline" onClick={() => setDialog('upload')}>
            <Upload aria-hidden="true" />{t('documents_item.upload')}
          </Button>
          <Button ref={linkRef} type="button" variant="outline" onClick={() => setDialog('link')}>
            <Link2 aria-hidden="true" />{t('documents_item.link')}
          </Button>
        </div>
      ) : null}
      {error ? <ProblemMessage problem={present(toProblem(error))} /> : null}
      {!answer.data ? (answer.error ? null : <div role="status" aria-busy="true" aria-label={t('documents_page.loading')} className="h-24 shimmer rounded-md border bg-muted" />)
        : rows.length === 0 ? <p className="text-muted-foreground">{t('documents_item.empty')}</p>
        : (
          <SortableTable<Row> caption={t('documents_item.title')} rows={rows} rowKey={(r) => `${r.doc.id}:${linkKey(r.link)}`}
            initialSort={{ id: 'date', desc: true }}
            columns={[
              { id: 'title', header: t('documents_item.col_title'), sort: (r) => r.doc.title, cell: title },
              { id: 'role', header: t('documents_item.col_role'), sort: role, cell: role },
              { id: 'date', header: t('documents_item.col_date'), sort: (r) => shownDate(r.doc), cell: date },
              { id: 'link', header: t('documents_item.col_link'), sort: linkedTo, cell: linkedTo },
              { id: 'menu', header: t('documents_item.col_actions'), align: 'end', cell: menu },
            ]}
            mobile={(r) => (
              <div className="space-y-1">
                <div className="flex items-center justify-between gap-2">{title(r)}{menu(r)}</div>
                <div className="flex justify-between gap-2 text-sm text-muted-foreground"><span>{[role(r), linkedTo(r)].join(' · ')}</span>{date(r)}</div>
              </div>
            )} />
        )}
      {removing ? (
        <Suspense fallback={null}>
          <ConfirmDialog label={t('documents_item.remove')} confirmation={t('documents_item.remove_confirm')} pending={unlink.isPending}
            onClose={() => setRemoving(null)} onConfirm={() => { setRemoving(null); remove(removing); }} />
        </Suspense>
      ) : null}
      {dialog === 'upload' ? <ItemUploadDialog itemId={itemId} defaultRole={defaultRole} onClose={() => setDialog(null)} onDone={done(uploadRef)} /> : null}
      {dialog === 'link' ? <ItemLinkDialog itemId={itemId} defaultRole={defaultRole} onClose={() => setDialog(null)} onDone={done(linkRef)} /> : null}
    </div>
  );
}

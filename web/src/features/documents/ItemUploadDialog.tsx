import { useQueryClient } from '@tanstack/react-query';
import { FileTextIcon, XIcon } from 'lucide-react';
import { useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { toProblem, type Problem } from '@/api/errors';
import { FormDialog } from '@/components/FormDialog';
import { DateField } from '@/components/form/DateField';
import { Field } from '@/components/form/Field';
import { SelectField } from '@/components/form/SelectField';
import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';
import { todayIso } from '@/lib/today';
import { invalidateDocuments } from '@/api/documents';
import { linkDocument, uploadDocument } from './api';
import { DuplicateNotice, duplicateOf, refusal, UNSUPPORTED, UploadZone } from './DocumentUpload';
import { fileSize, roleText, ROLES } from './format';
import '@/locales/documents';

const ISO = /^\d{4}-\d{2}-\d{2}$/;

type Row = { key: number; file: File; state?: 'done' | Problem };

/**
 * Files linked to the item at once, sent one after the other as on the documents page, with one type. A lone file can carry
 * a date (past only); several go without. A sent file stays listed and is not sent again; a failed one goes again on « Déposer ».
 * However many files, the list alone scrolls: the type, the date and the buttons stay in view. A file already in Nid (#297) waits:
 * the existing document linked with the chosen type, or the file added anyway. `targetKind` links to a person instead (#414: the tax notice).
 */
export function ItemUploadDialog({ itemId, targetKind = 'core.item', defaultRole, defaultDate = '', onClose, onDone }: {
  itemId: string; targetKind?: string; defaultRole: string; defaultDate?: string; onClose: () => void; onDone: (message: string) => void;
}) {
  const { t } = useTranslation();
  const { fetchClient } = useApi();
  const queryClient = useQueryClient();
  const [rows, setRows] = useState<Row[]>([]);
  const [role, setRole] = useState(defaultRole);
  const [date, setDate] = useState(defaultDate);
  const [dateError, setDateError] = useState<string | undefined>();
  const [sending, setSending] = useState(false);
  const next = useRef(0);
  const today = todayIso();

  const update = (key: number, state: Row['state']) => setRows((list) => list.map((r) => (r.key === key ? { ...r, state } : r)));
  const add = (files: File[], state?: Row['state']) => setRows((list) => [...list, ...files.map((file) => ({ key: next.current++, file, state }))]);
  const files = rows.filter((r) => r.state !== UNSUPPORTED);
  const queued = files.filter((r) => r.state !== 'done');
  const dated = files.length === 1 ? date : '';
  const totalSize = files.reduce((sum, r) => sum + r.file.size, 0);

  const send = async (done: (message?: string) => void) => {
    // ISO dates compare as strings.
    const invalid = !dated ? undefined : !ISO.test(dated) ? t('documents_page.edit_dialog.bad_date')
      : dated > today ? t('documents_page.edit_dialog.invalid_documentDate') : undefined;
    setDateError(invalid);
    if (invalid) return;
    setSending(true);
    let failed = false;
    for (const row of queued) {
      try {
        await uploadDocument(fetchClient, row.file, { targetKind, targetId: itemId, role, date: dated || undefined, warnDuplicate: 'true' });
        update(row.key, 'done');
      } catch (error) {
        failed = true;
        update(row.key, toProblem(error));
      }
    }
    void invalidateDocuments(queryClient);
    setSending(false);
    if (!failed) done(t('documents_item.uploaded', { count: files.length }));
  };

  // One duplicate settled; the dialog stays open, its button now « Fermer ».
  const settle = async (key: number, write: () => Promise<unknown>) => {
    setSending(true);
    try {
      await write();
      update(key, 'done');
    } catch (error) {
      const problem = toProblem(error);
      // Already linked to this item: nothing left to do.
      update(key, problem.code === 'documents.already_linked' ? 'done' : problem);
    }
    void invalidateDocuments(queryClient);
    setSending(false);
  };

  return (
    <FormDialog title={t('documents_item.upload_title')} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => (
        <form aria-labelledby={titleId} noValidate className="flex min-h-0 flex-col gap-3" onSubmit={(event) => { event.preventDefault(); event.stopPropagation(); void send(done); }}>
          {/* On a very short screen the body scrolls too; its padding keeps the focus rings from being clipped. */}
          <div className="-mx-1 flex min-h-0 flex-col gap-3 overflow-y-auto px-1 pb-1">
            <Field label={t('documents_item.file')}>
              {(control) => (
                <UploadZone {...control} disabled={sending} pick={t(files.length > 0 ? 'documents_item.add' : 'documents_page.upload.pick_many')} hint={t('documents_page.upload.types')}
                  summary={files.length > 0 ? t('documents_item.chosen', { count: files.length, size: fileSize(t, totalSize) }) : undefined}
                  onFiles={(picked) => add(picked)} onRefused={(picked) => add([picked], UNSUPPORTED)}>
                  {files.length > 0 && queued.length > 0 ? (
                    <Button type="button" variant="ghost" size="sm" disabled={sending} onClick={() => setRows((list) => list.filter((r) => r.state === 'done'))}>{t('documents_item.remove_all')}</Button>
                  ) : null}
                </UploadZone>
              )}
            </Field>
            {rows.length > 0 ? (
              <ul className={cn('divide-y overflow-y-auto rounded-md border text-sm', rows.length > 3 ? 'min-h-28' : 'shrink-0')}>
                {rows.map(({ key, file, state }) => {
                  const duplicate = typeof state === 'object' ? duplicateOf(state) : undefined;
                  return (
                    <li key={key} className="flex items-center gap-2 py-1 pr-1 pl-2">
                      <FileTextIcon aria-hidden="true" className="size-4 shrink-0 text-muted-foreground" />
                      {duplicate ? (
                        <DuplicateNotice name={file.name} document={duplicate} keep={t('documents_page.upload.link_existing')} disabled={sending}
                          onKeep={() => void settle(key, () => linkDocument(fetchClient, duplicate.id, targetKind, itemId, role))}
                          onAdd={() => void settle(key, () => uploadDocument(fetchClient, file, { targetKind, targetId: itemId, role, date: dated || undefined }))} />
                      ) : typeof state === 'object'
                        ? <p role="alert" className="min-w-0 flex-1 wrap-anywhere text-destructive">{t(`documents_page.upload.${refusal(state)}`, { name: file.name })}</p>
                        : (
                          <>
                            <span title={file.name} className="min-w-0 flex-1 truncate">{file.name}</span>
                            <span className="shrink-0 text-xs text-muted-foreground">{fileSize(t, file.size)}</span>
                          </>
                        )}
                      {state === 'done' ? <span className="shrink-0 px-1 py-1 text-xs text-status-good-text">{t('documents_page.upload.done')}</span> : (
                        <Button type="button" variant="ghost" size="icon" className="size-7 shrink-0" disabled={sending} aria-label={t('documents_item.remove_file', { name: file.name })}
                          onClick={() => setRows((list) => list.filter((r) => r.key !== key))}><XIcon aria-hidden="true" /></Button>
                      )}
                    </li>
                  );
                })}
              </ul>
            ) : null}
            <Field label={t('documents_item.col_role')}>
              {(control) => <SelectField {...control} value={role} onValueChange={setRole} options={ROLES.map((r) => ({ value: r, label: roleText(t, r) }))} />}
            </Field>
            {files.length > 1 ? <p className="text-sm text-muted-foreground">{t('documents_item.no_date')}</p> : (
              <Field label={t('documents_page.edit_dialog.field_date')} error={dateError}>
                {(control) => <DateField {...control} value={date} max={today} onChange={setDate} />}
              </Field>
            )}
          </div>
          <div className="flex shrink-0 justify-end gap-2">
            {/* Once a file is sent there is nothing left to cancel. */}
            <Button type="button" variant="outline" disabled={sending} onClick={() => done()}>{t(rows.some((r) => r.state === 'done') ? 'common.close' : 'documents_page.links.cancel')}</Button>
            <Button type="submit" disabled={sending || queued.length === 0}>{t('documents_item.upload_files', { count: queued.length })}</Button>
          </div>
        </form>
      )}
    </FormDialog>
  );
}

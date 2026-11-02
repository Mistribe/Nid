import { useQueryClient } from '@tanstack/react-query';
import { useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { toProblem, type Problem } from '@/api/errors';
import { FormDialog } from '@/components/FormDialog';
import { DateField } from '@/components/form/DateField';
import { Field } from '@/components/form/Field';
import { SelectField } from '@/components/form/SelectField';
import { Button } from '@/components/ui/button';
import { todayIso } from '../subscriptions/today';
import { invalidateDocuments, uploadDocument } from './api';
import { refusal, UNSUPPORTED, UploadZone } from './DocumentUpload';
import { roleText, ROLES } from './format';
import '@/locales/documents';

const ISO = /^\d{4}-\d{2}-\d{2}$/;

type Row = { key: number; file: File; state?: 'done' | Problem };

/**
 * Files linked to the item at once, sent one after the other as on the documents page, with one type. A lone file can carry
 * a date (past only); several go without. A sent file stays listed and is not sent again; a failed one goes again on « Déposer ».
 */
export function ItemUploadDialog({ itemId, defaultRole, onClose, onDone }: {
  itemId: string; defaultRole: string; onClose: () => void; onDone: (message: string) => void;
}) {
  const { t } = useTranslation();
  const { fetchClient } = useApi();
  const queryClient = useQueryClient();
  const [rows, setRows] = useState<Row[]>([]);
  const [role, setRole] = useState(defaultRole);
  const [date, setDate] = useState('');
  const [dateError, setDateError] = useState<string | undefined>();
  const [sending, setSending] = useState(false);
  const next = useRef(0);
  const today = todayIso();

  const update = (key: number, state: Row['state']) => setRows((list) => list.map((r) => (r.key === key ? { ...r, state } : r)));
  const add = (files: File[], state?: Row['state']) => setRows((list) => [...list, ...files.map((file) => ({ key: next.current++, file, state }))]);
  const files = rows.filter((r) => r.state !== UNSUPPORTED);
  const queued = files.filter((r) => r.state !== 'done');
  const dated = files.length === 1 ? date : '';

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
        await uploadDocument(fetchClient, row.file, { targetKind: 'core.item', targetId: itemId, role, date: dated || undefined });
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

  return (
    <FormDialog title={t('documents_item.upload_title')} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => (
        <form aria-labelledby={titleId} noValidate className="space-y-3" onSubmit={(event) => { event.preventDefault(); event.stopPropagation(); void send(done); }}>
          <Field label={t('documents_item.file')}>
            {(control) => <UploadZone {...control} disabled={sending} pick={t('documents_page.upload.pick_many')} hint={t('documents_page.upload.types')}
              onFiles={(picked) => add(picked)} onRefused={(picked) => add([picked], UNSUPPORTED)} />}
          </Field>
          {rows.length > 0 ? (
            <ul className="space-y-1 text-sm">
              {rows.map(({ key, file, state }) => (
                <li key={key} className="flex items-center justify-between gap-2">
                  {typeof state === 'object'
                    ? <p role="alert" className="min-w-0 text-destructive">{t(`documents_page.upload.${refusal(state)}`, { name: file.name })}</p>
                    : <span className="min-w-0 font-medium break-all">{file.name}</span>}
                  {state === 'done' ? <span className="text-status-good-text">{t('documents_page.upload.done')}</span> : (
                    <Button type="button" variant="ghost" size="sm" disabled={sending} aria-label={t('documents_item.remove_file', { name: file.name })}
                      onClick={() => setRows((list) => list.filter((r) => r.key !== key))}>{t('documents_item.remove')}</Button>
                  )}
                </li>
              ))}
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
          <div className="flex justify-end gap-2">
            {/* Once a file is sent there is nothing left to cancel. */}
            <Button type="button" variant="outline" disabled={sending} onClick={() => done()}>{t(rows.some((r) => r.state === 'done') ? 'common.close' : 'documents_page.links.cancel')}</Button>
            <Button type="submit" disabled={sending || queued.length === 0}>{t('documents_item.upload')}</Button>
          </div>
        </form>
      )}
    </FormDialog>
  );
}

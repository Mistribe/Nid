import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { toProblem, type Problem } from '@/api/errors';
import { FormDialog } from '@/components/FormDialog';
import { DateField } from '@/components/form/DateField';
import { Field } from '@/components/form/Field';
import { SelectField } from '@/components/form/SelectField';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { todayIso } from '../subscriptions/today';
import { invalidateDocuments, uploadDocument } from './api';
import { ACCEPT, refusal } from './DocumentUpload';
import { roleText, ROLES } from './format';
import '@/locales/documents';

const ISO = /^\d{4}-\d{2}-\d{2}$/;

/** A file linked to the item at once, with its type and an optional date (past only); a refused file reads as on the documents page. */
export function ItemUploadDialog({ itemId, defaultRole, onClose, onDone }: {
  itemId: string; defaultRole: string; onClose: () => void; onDone: (message: string) => void;
}) {
  const { t } = useTranslation();
  const { fetchClient } = useApi();
  const queryClient = useQueryClient();
  const [file, setFile] = useState<File | null>(null);
  const [role, setRole] = useState(defaultRole);
  const [date, setDate] = useState('');
  const [dateError, setDateError] = useState<string | undefined>();
  const [problem, setProblem] = useState<Problem | null>(null);
  const [pending, setPending] = useState(false);
  const today = todayIso();

  const send = async (done: (message?: string) => void) => {
    if (!file) return;
    // ISO dates compare as strings.
    const invalid = !date ? undefined : !ISO.test(date) ? t('documents_page.edit_dialog.bad_date')
      : date > today ? t('documents_page.edit_dialog.invalid_documentDate') : undefined;
    setDateError(invalid);
    setProblem(null);
    if (invalid) return;
    setPending(true);
    try {
      await uploadDocument(fetchClient, file, { targetKind: 'core.item', targetId: itemId, role, date: date || undefined });
      void invalidateDocuments(queryClient);
      done(t('documents_item.uploaded'));
    } catch (error) {
      setProblem(toProblem(error));
    } finally {
      setPending(false);
    }
  };

  return (
    <FormDialog title={t('documents_item.upload_title')} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => (
        <form aria-labelledby={titleId} noValidate className="space-y-3" onSubmit={(event) => { event.preventDefault(); event.stopPropagation(); void send(done); }}>
          {problem && file ? <p role="alert" className="text-sm text-destructive">{t(`documents_page.upload.${refusal(problem)}`, { name: file.name })}</p> : null}
          <Field label={t('documents_item.file')}>
            {(control) => <Input {...control} type="file" accept={ACCEPT} onChange={(e) => { setFile(e.currentTarget.files?.[0] ?? null); setProblem(null); }} />}
          </Field>
          <Field label={t('documents_item.col_role')}>
            {(control) => <SelectField {...control} value={role} onValueChange={setRole} options={ROLES.map((r) => ({ value: r, label: roleText(t, r) }))} />}
          </Field>
          <Field label={t('documents_page.edit_dialog.field_date')} error={dateError}>
            {(control) => <DateField {...control} value={date} max={today} onChange={setDate} />}
          </Field>
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => done()}>{t('documents_page.links.cancel')}</Button>
            <Button type="submit" disabled={!file || pending}>{t('documents_item.upload')}</Button>
          </div>
        </form>
      )}
    </FormDialog>
  );
}

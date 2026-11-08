import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { present, toProblem } from '@/api/errors';
import { FormDialog } from '@/components/FormDialog';
import { DateField } from '@/components/form/DateField';
import { Field } from '@/components/form/Field';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Textarea } from '@/components/ui/textarea';
import { todayIso } from '@/lib/today';
import { type DocumentDetail } from '@/api/documents';
import { useEditDocument } from './api';
import '@/locales/documents';

type Values = { title: string; documentDate: string; note: string };
type Errors = Partial<Record<keyof Values, string>>;
const FIELDS = ['title', 'documentDate', 'note'] as const;
const ISO = /^\d{4}-\d{2}-\d{2}$/;

/** Title, date (past only) and note. The server checks the bounds; a refusal it pins on a field shows on that field. */
export function EditDocumentDialog({ detail, onClose, onDone }: { detail: DocumentDetail; onClose: () => void; onDone: (message: string) => void }) {
  const { t } = useTranslation();
  const edit = useEditDocument();
  const doc = detail.document;
  const [values, setValues] = useState<Values>({ title: doc.title, documentDate: doc.documentDate ?? '', note: detail.note ?? '' });
  const [errors, setErrors] = useState<Errors>({});
  const [problem, setProblem] = useState<unknown>(null);
  const today = todayIso();
  const set = (key: keyof Values) => (value: string) => setValues((v) => ({ ...v, [key]: value }));

  const save = (done: (message?: string) => void) => {
    const date = values.documentDate;
    // ISO dates compare as strings.
    const dateError = !date ? undefined : !ISO.test(date) ? t('documents_page.edit_dialog.bad_date')
      : date > today ? t('documents_page.edit_dialog.invalid_documentDate') : undefined;
    setErrors(dateError ? { documentDate: dateError } : {});
    setProblem(null);
    if (dateError) return;
    edit.mutate({ params: { path: { id: doc.id } }, body: { title: values.title, documentDate: date || null, note: values.note || null } }, {
      onSuccess: () => done(t('documents_page.edit_dialog.saved')),
      onError: (error) => {
        const placed = (toProblem(error).errors ?? []).flatMap((e) => FIELDS.find((f) => f === e.path) ?? []);
        setErrors(Object.fromEntries(placed.map((f) => [f, t(`documents_page.edit_dialog.invalid_${f}`)])));
        if (placed.length === 0) setProblem(error);
      },
    });
  };

  return (
    <FormDialog title={t('documents_page.edit_dialog.title')} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => (
        <form aria-labelledby={titleId} noValidate className="space-y-3" onSubmit={(event) => { event.preventDefault(); event.stopPropagation(); save(done); }}>
          {problem ? <ProblemMessage problem={present(toProblem(problem))} /> : null}
          <Field label={t('documents_page.edit_dialog.field_title')} error={errors.title}>
            {(control) => <Input {...control} value={values.title} maxLength={255} onChange={(e) => set('title')(e.target.value)} />}
          </Field>
          <Field label={t('documents_page.edit_dialog.field_date')} error={errors.documentDate}>
            {(control) => <DateField {...control} value={values.documentDate} max={today} onChange={set('documentDate')} />}
          </Field>
          <Field label={t('documents_page.edit_dialog.field_note')} error={errors.note}>
            {(control) => <Textarea {...control} value={values.note} maxLength={2000} onChange={(e) => set('note')(e.target.value)} />}
          </Field>
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => done()}>{t('documents_page.edit_dialog.cancel')}</Button>
            <Button type="submit" disabled={edit.isPending}>{t('documents_page.edit_dialog.save')}</Button>
          </div>
        </form>
      )}
    </FormDialog>
  );
}

import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { present, toProblem } from '@/api/errors';
import { FormDialog } from '@/components/FormDialog';
import { Field } from '@/components/form/Field';
import { fieldErrorsOf } from '@/components/form/fieldErrors';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Textarea } from '@/components/ui/textarea';
import type { BankTransaction } from '@/api/banking';
import { useLineActions } from './api';
import '@/locales/banks';

/** A line's own note (up to 500 characters); an empty one clears it. */
export default function NoteDialog({ line, onClose, onDone }: { line: BankTransaction; onClose: () => void; onDone: (message: string) => void }) {
  const { t } = useTranslation();
  const { setNote } = useLineActions();
  const [note, setText] = useState(line.note ?? '');
  const field = fieldErrorsOf(setNote.error).find((f) => f.path === 'note');
  return (
    <FormDialog title={t('banks.operations.note_title')} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => (
        <form aria-labelledby={titleId} noValidate className="space-y-4" onSubmit={(event) => {
          event.preventDefault();
          event.stopPropagation();
          setNote.mutate({ params: { path: { id: line.id } }, body: { note: note.trim() || null } }, { onSuccess: () => done(t('banks.operations.done_note')) });
        }}>
          <p className="text-sm text-muted-foreground">{line.label}</p>
          {setNote.error && !field ? <ProblemMessage problem={present(toProblem(setNote.error))} /> : null}
          <Field label={t('banks.operations.note_field')} error={field?.message}>
            {(c) => <Textarea {...c} value={note} maxLength={500} onChange={(event) => setText(event.target.value)} />}
          </Field>
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => done()}>{t('common.cancel')}</Button>
            <Button type="submit" disabled={setNote.isPending}>{t('banks.operations.note_submit')}</Button>
          </div>
        </form>
      )}
    </FormDialog>
  );
}

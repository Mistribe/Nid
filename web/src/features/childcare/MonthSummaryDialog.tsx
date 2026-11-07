import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { FormDialog } from '@/components/FormDialog';
import { Field } from '@/components/form/Field';
import { SelectField } from '@/components/form/SelectField';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { ABSENCE_REASONS, reasonLabel } from './statementText';
import '@/locales/childcare';

export type MonthSummary = { careDays: number; hours: string; absence: string };

/** Decision 29: a past month in two figures; the server lays them over the template's days. */
export function MonthSummaryDialog({ error, pending, onSave, onClose }: {
  error: string; pending: boolean; onSave: (summary: MonthSummary) => void; onClose: () => void;
}) {
  const { t } = useTranslation();
  const [careDays, setCareDays] = useState('');
  const [hours, setHours] = useState('');
  const [absence, setAbsence] = useState('family');
  const [local, setLocal] = useState('');
  return (
    <FormDialog title={t('childcare_statement.summary.title')} onClose={onClose} onDone={onClose}>
      {({ titleId, done }) => (
        <form aria-labelledby={titleId} noValidate className="space-y-3" onSubmit={(event) => {
          event.preventDefault();
          event.stopPropagation();
          const total = hours.trim().replace(',', '.');
          if (!/^\d+$/.test(careDays.trim()) || !/^\d+(\.\d{1,2})?$/.test(total)) { setLocal(t('childcare_statement.summary.format')); return; }
          setLocal('');
          onSave({ careDays: Number(careDays), hours: total, absence });
        }}>
          {local || error ? <p role="alert" className="text-sm text-destructive">{local || error}</p> : null}
          <p className="text-sm text-muted-foreground">{t('childcare_statement.summary.intro')}</p>
          <div className="flex flex-wrap gap-3">
            <Field label={t('childcare_statement.summary.care_days')}>{(c) => <Input {...c} inputMode="numeric" className="w-36" value={careDays} onChange={(e) => setCareDays(e.target.value)} />}</Field>
            <Field label={t('childcare_statement.summary.hours')}>{(c) => <Input {...c} inputMode="decimal" className="w-36" value={hours} onChange={(e) => setHours(e.target.value)} />}</Field>
          </div>
          <Field label={t('childcare_statement.summary.absence')}>
            {(c) => <SelectField {...c} value={absence} onValueChange={setAbsence}
              options={ABSENCE_REASONS.filter((r) => r !== 'holiday').map((r) => ({ value: r, label: reasonLabel(t, r) }))} />}
          </Field>
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => done()}>{t('childcare_statement.cancel')}</Button>
            <Button type="submit" disabled={pending}>{t('childcare_statement.summary.save')}</Button>
          </div>
        </form>
      )}
    </FormDialog>
  );
}

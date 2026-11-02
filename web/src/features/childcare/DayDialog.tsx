import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { FormDialog } from '@/components/FormDialog';
import { Field } from '@/components/form/Field';
import { Segmented } from '@/components/form/Segmented';
import { SelectField } from '@/components/form/SelectField';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { Input } from '@/components/ui/input';
import type { StatementDay, StatementDayBody } from './api';
import { ABSENCE_REASONS, dayTitle, reasonLabel } from './statementText';
import '@/locales/childcare';

/**
 * One day's deviation: present with its times and meal, or absent with a reason when it was planned. The time inputs are
 * `Input type="time"`, as the template's: shadcn/ui has no time picker.
 */
export function DayDialog({ day, error, pending, onSave, onClose }: {
  day: StatementDay; error: string; pending: boolean; onSave: (day: StatementDayBody) => void; onClose: () => void;
}) {
  const { t } = useTranslation();
  const [present, setPresent] = useState(day.present);
  const [arrival, setArrival] = useState(day.arrival ?? day.templateArrival ?? '');
  const [departure, setDeparture] = useState(day.departure ?? day.templateDeparture ?? '');
  const [meal, setMeal] = useState(day.meal);
  const [absence, setAbsence] = useState(day.absence ?? '');
  const [local, setLocal] = useState('');
  return (
    <FormDialog title={dayTitle(day.date)} onClose={onClose} onDone={onClose}>
      {({ titleId, done }) => (
        <form aria-labelledby={titleId} noValidate className="space-y-3" onSubmit={(event) => {
          event.preventDefault();
          event.stopPropagation();
          if (present && !(arrival && departure && departure > arrival)) { setLocal(t('childcare_statement.problem.childcare_departure_before_arrival')); return; }
          if (!present && day.planned && !absence) { setLocal(t('childcare_statement.problem.childcare_absence_required')); return; }
          setLocal('');
          onSave(present
            ? { date: day.date, present: true, arrival, departure, meal, absence: null }
            : { date: day.date, present: false, arrival: null, departure: null, meal: false, absence: day.planned ? absence : null });
        }}>
          {local || error ? <p role="alert" className="text-sm text-destructive">{local || error}</p> : null}
          <Segmented aria-label={t('childcare_statement.presence')} value={present ? 'present' : 'absent'} onValueChange={(v) => setPresent(v === 'present')}
            options={[{ value: 'present', label: t('childcare_statement.present') }, { value: 'absent', label: t('childcare_statement.absent') }]} />
          {present ? (
            <div className="flex flex-wrap gap-3">
              <Field label={t('childcare_statement.arrival')}>{(c) => <Input {...c} type="time" className="w-28" value={arrival} onChange={(e) => setArrival(e.target.value)} />}</Field>
              <Field label={t('childcare_statement.departure')}>{(c) => <Input {...c} type="time" className="w-28" value={departure} onChange={(e) => setDeparture(e.target.value)} />}</Field>
              <label className="flex items-center gap-2 self-end pb-2 text-sm">
                <Checkbox checked={meal} onCheckedChange={(v) => setMeal(v === true)} />
                {t('childcare_statement.meal')}
              </label>
            </div>
          ) : day.planned ? (
            <Field label={t('childcare_statement.absence')}>
              {(c) => <SelectField {...c} value={absence} onValueChange={setAbsence} placeholder={t('childcare_statement.absence')}
                options={ABSENCE_REASONS.map((r) => ({ value: r, label: reasonLabel(t, r) }))} />}
            </Field>
          ) : null}
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => done()}>{t('childcare_statement.cancel')}</Button>
            <Button type="submit" disabled={pending}>{t('childcare_statement.save_day')}</Button>
          </div>
        </form>
      )}
    </FormDialog>
  );
}

import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { FormDialog } from '@/components/FormDialog';
import { ProblemMessage } from '@/components/ProblemMessage';
import { DateField } from '@/components/form/DateField';
import { Field } from '@/components/form/Field';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { PERSONS } from '@/api/persons';
import { fieldErrorsOf, messageFor } from '@/components/form/fieldErrors';
import { todayIso } from '@/lib/today';
import { PERSON_RETIREMENT, PROJECTION } from './api';

/** A whole number as the server checks it: anything else goes as -1, which it refuses on that field. */
const ageOf = (text: string) => (/^\d+$/.test(text.trim()) ? Number(text) : -1);

/** A person's birth date and retirement age (« Modifier » of the Retraite section). */
export function RetirementDialog({ person, onClose, onDone }: {
  person: { id: string; displayName: string; birthDate?: string | null; retirementAge: number }; onClose: () => void; onDone: (message: string) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const save = $api.useMutation('put', PERSON_RETIREMENT);
  const [birthDate, setBirthDate] = useState(person.birthDate ?? '');
  const [age, setAge] = useState(String(person.retirementAge));

  const fields = fieldErrorsOf(save.error);
  const errorOf = (path: string) => { const f = fields.find((x) => x.path === path); return f ? messageFor(t, f) : undefined; };
  const unplaced = save.error && !(fields.length > 0 && fields.every((f) => f.path === 'birthDate' || f.path === 'retirementAge'));

  return (
    <FormDialog title={t('settings.projection.retirement_of', { name: person.displayName })} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => (
        <form aria-labelledby={titleId} noValidate className="space-y-3" onSubmit={(event) => {
          event.preventDefault();
          event.stopPropagation();
          save.mutate({ params: { path: { id: person.id } }, body: { birthDate: birthDate === '' ? null : birthDate, retirementAge: ageOf(age) } }, {
            onSuccess: () => {
              void queryClient.invalidateQueries({ queryKey: ['get', PERSONS] });
              void queryClient.invalidateQueries({ queryKey: ['get', PROJECTION] });
              done(t('settings.projection.retirement_saved', { name: person.displayName }));
            },
          });
        }}>
          {unplaced ? <ProblemMessage problem={present(toProblem(save.error))} /> : null}
          <Field label={t('settings.projection.birth_date')} error={errorOf('birthDate')}>
            {(c) => <DateField {...c} value={birthDate} onChange={setBirthDate} max={todayIso()} />}
          </Field>
          <div className="max-w-48">
            <Field label={t('settings.projection.retirement_age')} error={errorOf('retirementAge')}>
              {(c) => <Input {...c} type="number" inputMode="numeric" min={50} max={75} step={1} value={age} onChange={(e) => setAge(e.target.value)} />}
            </Field>
          </div>
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => done()}>{t('common.cancel')}</Button>
            <Button type="submit" disabled={save.isPending}>{t('settings.projection.save')}</Button>
          </div>
        </form>
      )}
    </FormDialog>
  );
}

import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { AccessBadge } from '@/components/AccessBadge';
import { FormDialog } from '@/components/FormDialog';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Field } from '@/components/form/Field';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { PERSONS } from '@/features/subscriptions/api';
import { SettingsFrame } from './SettingsFrame';
import '@/locales/notifications';

/** screens.md §9, Réglages › Personnes: the household's persons, and « Ajouter un enfant » (a `dependent`). */
export function PersonsPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const persons = $api.useQuery('get', PERSONS);
  const [adding, setAdding] = useState(false);

  return (
    <SettingsFrame title={t('settings_frame.nav.persons')}
      actions={<Button variant="outline" onClick={() => setAdding(true)}>{t('settings.persons.add_child')}</Button>}>
      {persons.error ? <ProblemMessage problem={present(toProblem(persons.error))} /> : null}
      <ul aria-label={t('settings_frame.nav.persons')} className="space-y-1">
        {persons.data?.map((person) => (
          <li key={person.id} className="flex items-center gap-2">
            {person.displayName}
            {person.kind === 'dependent' ? <AccessBadge access="dependent" /> : null}
          </li>
        ))}
      </ul>
      {adding ? <AddChildDialog onClose={() => setAdding(false)} onDone={(message) => { setAdding(false); toast.success(message); }} /> : null}
    </SettingsFrame>
  );
}

function AddChildDialog({ onClose, onDone }: { onClose: () => void; onDone: (message: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const create = $api.useMutation('post', '/api/v1/core/persons/dependents');
  const problem = create.error ? present(toProblem(create.error)) : null;

  return (
    <FormDialog title={t('settings.persons.add_child')} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => (
        // No browser check: the server's refusal of an empty name is what the person reads (issue #24).
        <form aria-labelledby={titleId} noValidate className="space-y-3" onSubmit={(event) => {
          event.preventDefault();
          event.stopPropagation();
          const displayName = (new FormData(event.currentTarget).get('displayName') as string | null) ?? '';
          create.mutate({ body: { displayName } }, {
            onSuccess: (person) => {
              // The owner pickers and chips read the same list.
              void queryClient.invalidateQueries({ queryKey: ['get', PERSONS] });
              done(t('settings.persons.added', { name: person.displayName }));
            },
          });
        }}>
          {problem ? <ProblemMessage problem={problem} /> : null}
          <Field label={t('settings.persons.name')}>{(c) => <Input {...c} name="displayName" autoComplete="off" />}</Field>
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => done()}>{t('common.cancel')}</Button>
            <Button type="submit" disabled={create.isPending}>{t('settings.persons.add')}</Button>
          </div>
        </form>
      )}
    </FormDialog>
  );
}

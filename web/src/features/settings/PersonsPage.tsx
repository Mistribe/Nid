import { useQueryClient } from '@tanstack/react-query';
import type { TFunction } from 'i18next';
import { useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { PERSONS } from '@/api/persons';
import type { components } from '@/api/schema';
import { ConfirmFromMenu } from '@/components/ConfirmAction';
import { FormDialog } from '@/components/FormDialog';
import { monthText } from '@/components/LocalDate';
import { ProblemMessage } from '@/components/ProblemMessage';
import { DateField } from '@/components/form/DateField';
import { Field } from '@/components/form/Field';
import { fieldErrorsOf, messageFor } from '@/components/form/fieldErrors';
import { RowMenu } from '@/components/page/RowMenu';
import { SortableTable } from '@/components/page/SortableTable';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Skeleton } from '@/components/ui/skeleton';
import { todayIso } from '@/lib/today';
import { RetirementDialog } from './RetirementDialog';
import { SettingsFrame } from './SettingsFrame';
import '@/locales/notifications';

type Person = components['schemas']['PersonDto'];

const DEPENDENTS = '/api/v1/core/persons/dependents' as const;
const DEPENDENT = '/api/v1/core/persons/dependents/{id}' as const;
const DEPENDENT_NAME = '/api/v1/core/persons/dependents/{id}/name' as const;

const isChild = (p: Person) => p.kind === 'dependent';

/** yyyy-mm-dd as jj/mm/aaaa, the way the date fields read it (a string swap). */
const dayFirst = (iso: string) => iso.split('-').reverse().join('/');

/** « 64 ans · mars 2052 »: the stored age, and the month the server computed from the birth date. */
function retirementText(t: TFunction, p: Person) {
  if (isChild(p)) return t('settings.persons.none');
  const age = p.retirementMonths > 0
    ? t('settings.persons.years_months', { years: p.retirementAge, months: p.retirementMonths })
    : t('settings.persons.years', { count: p.retirementAge });
  return p.retirementOn ? `${age} · ${monthText(p.retirementOn)}` : age;
}

const accessText = (t: TFunction, p: Person) =>
  t(isChild(p) ? 'settings.persons.access_none' : p.signedIn ? 'settings.persons.access_active' : 'settings.persons.access_pending');

/** A removal refused while something names the child, in the reader's words (the server sends the kinds, never an item); null for any other failure. */
function inUseText(t: TFunction, error: unknown, name: string): string | null {
  const problem = toProblem(error);
  const uses = (problem as { uses?: unknown }).uses;
  if (problem.code !== 'core.person_in_use' || !Array.isArray(uses)) return null;
  const list = new Intl.ListFormat('fr', { type: 'conjunction' }).format(uses.map((u) => t(`settings.persons.use.${String(u)}`, { defaultValue: String(u) })));
  return t('settings.persons.in_use', { name, uses: list });
}

type Dialog = { kind: 'add' } | { kind: 'dates' | 'rename' | 'remove'; person: Person };

/** screens.md §9, Réglages › Personnes: the household's persons, each one's birth date, age and retirement; children are added, renamed and removed here. */
export function PersonsPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const persons = $api.useQuery('get', PERSONS);
  const remove = $api.useMutation('delete', DEPENDENT);
  const [dialog, setDialog] = useState<Dialog | null>(null);
  const [removing, setRemoving] = useState('');
  // The dialogs open from a row's ⋯ (in the table or the phone list): the focus comes back to « Ajouter un enfant ».
  const addRef = useRef<HTMLButtonElement>(null);
  const close = () => { setDialog(null); addRef.current?.focus(); };
  const announce = (message: string) => { close(); toast.success(message); };

  const menu = (p: Person) => (
    <RowMenu label={t('settings.persons.menu', { name: p.displayName })} items={isChild(p) ? [
      { label: t('settings.persons.edit_birth_date'), onSelect: () => setDialog({ kind: 'dates', person: p }) },
      { label: t('settings.persons.rename'), onSelect: () => setDialog({ kind: 'rename', person: p }) },
      { separator: true },
      { label: t('settings.persons.remove'), destructive: true, onSelect: () => { remove.reset(); setDialog({ kind: 'remove', person: p }); } },
    ] : [
      { label: t('settings.persons.edit_retirement'), onSelect: () => setDialog({ kind: 'dates', person: p }) },
    ]} />
  );
  const role = (p: Person) => t(isChild(p) ? 'settings.persons.role_child' : 'settings.persons.role_adult');
  const birth = (p: Person) => (p.birthDate ? dayFirst(p.birthDate) : t('settings.persons.none'));
  const age = (p: Person) => (p.age != null ? t('settings.persons.years', { count: p.age }) : t('settings.persons.none'));
  const inUse = remove.error ? inUseText(t, remove.error, removing) : null;

  return (
    <SettingsFrame title={t('settings_frame.nav.persons')}
      actions={<Button ref={addRef} variant="outline" onClick={() => setDialog({ kind: 'add' })}>{t('settings.persons.add_child')}</Button>}>
      <p className="text-sm text-muted-foreground">{t('settings.persons.help')}</p>
      {persons.error ? <ProblemMessage problem={present(toProblem(persons.error))} /> : null}
      {inUse ? <p role="alert" className="rounded-md border border-destructive p-3 text-sm">{inUse}</p>
        : remove.error ? <ProblemMessage problem={present(toProblem(remove.error))} /> : null}
      {persons.data ? (
        <SortableTable<Person> caption={t('settings_frame.nav.persons')} rows={persons.data} rowKey={(p) => p.id}
          columns={[
            { id: 'name', header: t('settings.persons.col_name'), sort: (p) => p.displayName, cell: (p) => <span className="font-medium">{p.displayName}</span> },
            { id: 'role', header: t('settings.persons.col_role'), sort: role, cell: role },
            { id: 'birth', header: t('settings.persons.col_birth'), cell: (p) => <span className="tabular-nums">{birth(p)}</span> },
            { id: 'age', header: t('settings.persons.col_age'), sort: (p) => p.age ?? Number.NaN, cell: age, align: 'end' },
            { id: 'retirement', header: t('settings.persons.col_retirement'), cell: (p) => retirementText(t, p) },
            { id: 'access', header: t('settings.persons.col_access'), cell: (p) => accessText(t, p) },
            { id: 'menu', header: t('settings.persons.col_actions'), cell: menu, align: 'end' },
          ]}
          mobile={(p) => (
            <div className="space-y-1">
              <div className="flex items-center justify-between gap-2">
                <span className="font-medium">{p.displayName}</span>
                <span className="flex items-center gap-2 text-sm text-muted-foreground">{role(p)}{menu(p)}</span>
              </div>
              <p className="text-sm text-muted-foreground">{[age(p), isChild(p) ? null : retirementText(t, p)].filter(Boolean).join(' · ')}</p>
            </div>
          )} />
      ) : persons.error ? null : <Skeleton aria-busy="true" className="h-32"><span className="sr-only">{t('settings.persons.loading')}</span></Skeleton>}
      {dialog?.kind === 'add' ? <AddChildDialog onClose={close} onDone={announce} /> : null}
      {dialog?.kind === 'dates' ? <RetirementDialog person={dialog.person} birthOnly={isChild(dialog.person)} onClose={close} onDone={announce} /> : null}
      {dialog?.kind === 'rename' ? <RenameDialog person={dialog.person} onClose={close} onDone={announce} /> : null}
      {dialog?.kind === 'remove' ? (
        <ConfirmFromMenu label={t('settings.persons.remove_title', { name: dialog.person.displayName })} returnFocus={addRef}
          confirmation={t('settings.persons.remove_confirm', { name: dialog.person.displayName })} pending={remove.isPending}
          onClose={() => setDialog(null)}
          onConfirm={() => {
            const { id, displayName } = dialog.person;
            setRemoving(displayName);
            remove.mutate({ params: { path: { id } } }, {
              onSuccess: () => {
                // The owner pickers and chips read the same list.
                void queryClient.invalidateQueries({ queryKey: ['get', PERSONS] });
                toast.success(t('settings.persons.removed', { name: displayName }));
              },
            });
          }} />
      ) : null}
    </SettingsFrame>
  );
}

function AddChildDialog({ onClose, onDone }: { onClose: () => void; onDone: (message: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const create = $api.useMutation('post', DEPENDENTS);
  const [birthDate, setBirthDate] = useState('');
  const fields = fieldErrorsOf(create.error);
  const birthError = fields.find((f) => f.path === 'birthDate');
  const unplaced = create.error && !(fields.length > 0 && fields.every((f) => f.path === 'birthDate'));

  return (
    <FormDialog title={t('settings.persons.add_child')} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => (
        // No browser check: the server's refusal of an empty name is what the person reads (issue #24).
        <form aria-labelledby={titleId} noValidate className="space-y-3" onSubmit={(event) => {
          event.preventDefault();
          event.stopPropagation();
          const displayName = (new FormData(event.currentTarget).get('displayName') as string | null) ?? '';
          create.mutate({ body: { displayName, birthDate: birthDate === '' ? null : birthDate } }, {
            onSuccess: (person) => {
              void queryClient.invalidateQueries({ queryKey: ['get', PERSONS] });
              done(t('settings.persons.added', { name: person.displayName }));
            },
          });
        }}>
          {unplaced ? <ProblemMessage problem={present(toProblem(create.error))} /> : null}
          <Field label={t('settings.persons.name')}>{(c) => <Input {...c} name="displayName" autoComplete="off" />}</Field>
          <Field label={t('settings.persons.birth_date_optional')} hint={t('settings.persons.birth_date_hint')} error={birthError ? messageFor(t, birthError) : undefined}>
            {(c) => <DateField {...c} value={birthDate} onChange={setBirthDate} max={todayIso()} />}
          </Field>
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => done()}>{t('common.cancel')}</Button>
            <Button type="submit" disabled={create.isPending}>{t('settings.persons.add')}</Button>
          </div>
        </form>
      )}
    </FormDialog>
  );
}

function RenameDialog({ person, onClose, onDone }: { person: Person; onClose: () => void; onDone: (message: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const rename = $api.useMutation('put', DEPENDENT_NAME);
  const [name, setName] = useState(person.displayName);
  const fields = fieldErrorsOf(rename.error);
  const nameError = fields.find((f) => f.path === 'displayName');

  return (
    <FormDialog title={t('settings.persons.rename_title', { name: person.displayName })} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => (
        <form aria-labelledby={titleId} noValidate className="space-y-3" onSubmit={(event) => {
          event.preventDefault();
          event.stopPropagation();
          rename.mutate({ params: { path: { id: person.id } }, body: { displayName: name } }, {
            onSuccess: (renamed) => {
              void queryClient.invalidateQueries({ queryKey: ['get', PERSONS] });
              done(t('settings.persons.renamed', { name: renamed.displayName }));
            },
          });
        }}>
          {rename.error && !nameError ? <ProblemMessage problem={present(toProblem(rename.error))} /> : null}
          <Field label={t('settings.persons.name')} error={nameError ? messageFor(t, nameError) : undefined}>
            {(c) => <Input {...c} autoComplete="off" value={name} onChange={(e) => setName(e.target.value)} />}
          </Field>
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => done()}>{t('common.cancel')}</Button>
            <Button type="submit" disabled={rename.isPending}>{t('settings.persons.save')}</Button>
          </div>
        </form>
      )}
    </FormDialog>
  );
}

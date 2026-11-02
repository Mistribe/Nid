import { buttonVariants } from '@/components/ui/button';
import { Link, useNavigate } from '@tanstack/react-router';
import { useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem, type Presentation } from '@/api/errors';
import type { components } from '@/api/schema';
import { Field } from '@/components/form/Field';
import { SelectField } from '@/components/form/SelectField';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import type { ReadyFile } from './draft';
import { StepCard } from './StepCard';
import { useRefreshImports } from './queries';
import { ACTIVE_STATES, effectiveState } from './states';
import '@/locales/import';

type Person = components['schemas']['PersonDto'];

/** The server's own fallback (import-format.md §12bis): an adult is resolved by email when the map says nothing. */
function matchByEmail(adults: Person[], email: string | null): string | undefined {
  if (!email) return undefined;
  const wanted = email.toLowerCase();
  return adults.find((person) => person.email?.toLowerCase() === wanted)?.id;
}

/** Step 3 of web design §6.2, then the session: each file adult mapped to a household person, prefilled by email. */
export function StartImport({ file }: { file: ReadyFile }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const navigate = useNavigate();
  const persons = $api.useQuery('get', '/api/v1/core/persons');
  const [chosen, setChosen] = useState<Record<string, string>>({});
  const [problem, setProblem] = useState<Presentation | null>(null);
  const [active, setActive] = useState(false);
  const refresh = useRefreshImports();

  const adults = (persons.data ?? []).filter((person) => person.kind === 'adult');
  const choice = (ref: string, email: string | null) => chosen[ref] ?? matchByEmail(adults, email) ?? '';

  const start = $api.useMutation('post', '/api/v1/platform/imports', {
    onSuccess: (session) => {
      refresh();
      void navigate({ to: '/import/$id', params: { id: session.id } });
    },
    onError: (error) => {
      const failure = toProblem(error);
      if (failure.code === 'import.session_active') {
        setProblem(null);
        setActive(true);
      } else setProblem(present(failure));
    },
  });

  const submit = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    const map = Object.fromEntries(
      file.adults.map((adult) => [adult.ref, choice(adult.ref, adult.email)] as const).filter(([, id]) => id !== ''),
    );
    start.mutate({ body: { format: file.format, version: file.version, adults: map } });
  };

  return (
    <StepCard>
    <form onSubmit={submit} className="space-y-4">
      <fieldset className="space-y-3">
        <legend className="text-lg font-semibold">{t('import.adults_title')}</legend>
        <p className="text-sm text-muted-foreground">{t('import.adults_hint')}</p>
        {persons.error ? <ProblemMessage problem={present(toProblem(persons.error))} /> : null}
        {file.adults.length === 0 ? <p>{t('import.adults_empty')}</p> : null}
        {file.adults.map((adult) => (
          <div key={adult.ref} className="max-w-md">
            <Field label={t('import.adult_label', { name: adult.displayName, ref: adult.ref })}>
              {(c) => (
                <SelectField {...c} value={choice(adult.ref, adult.email)} onValueChange={(v) => setChosen({ ...chosen, [adult.ref]: v })}
                  options={[
                    { value: '', label: t('import.adults_none') },
                    ...adults.map((person) => ({
                      value: person.id,
                      label: person.email ? t('import.person_option', { name: person.displayName, email: person.email }) : person.displayName,
                    })),
                  ]} />
              )}
            </Field>
          </div>
        ))}
      </fieldset>
      {problem ? <ProblemMessage problem={problem} /> : null}
      {active ? <ActiveImport /> : null}
      <Button type="submit" disabled={start.isPending || persons.isPending}>{t('import.start')}</Button>
    </form>
    </StepCard>
  );
}

/** One active import per person (import-format.md §11.5): point to it rather than explain a 409. */
function ActiveImport() {
  const { t } = useTranslation();
  const { $api } = useApi();
  // Read again whenever the refusal appears: the active import may be one this tab started since the list was read.
  const list = $api.useQuery('get', '/api/v1/platform/imports', undefined, { refetchOnMount: 'always' });
  const current = list.data?.find((session) => ACTIVE_STATES.has(effectiveState(session)));
  return (
    <div role="alert" className="space-y-1 rounded-md border border-destructive p-3">
      <p>{t('import.session_active')}</p>
      {current ? (
        <Link to="/import/$id" params={{ id: current.id }} className={buttonVariants({ variant: 'outline', size: 'sm' })}>{t('import.resume_active')}</Link>
      ) : null}
    </div>
  );
}

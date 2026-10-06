import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useId, useRef } from 'react';
import { useFieldArray, useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { useSession } from '@/app/Providers';
import { OwnershipFields } from '@/components/ownership/OwnershipFields';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { PERSONS } from '../subscriptions/api';
import { Field } from '../subscriptions/edits/Field';
import { useSubmitErrors } from '../subscriptions/edits/useSubmitErrors';
import { invalidateIncome, SOURCES } from './api';
import { KINDS } from './format';
import { newIncomePath, newIncomeSchema, toCreateBody, type NewIncomeValues } from './newIncomeSchema';
import { newPhase } from './phases/phase';
import { PhaseFields } from './phases/PhaseFields';

const input = 'rounded-md border px-3 py-2';

/** 2C2a design §5: a source, its visibility answered explicitly, and its components, in one request. */
export function NewIncomeSource() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { session } = useSession();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const people = $api.useQuery('get', PERSONS);
  const visibilityErrorId = useId();
  const componentsId = useId();
  const addRef = useRef<HTMLButtonElement>(null);
  const form = useForm<NewIncomeValues>({
    resolver: zodResolver(newIncomeSchema(t)),
    // `visibility` is left out on purpose: neither answer is checked until the person picks one.
    defaultValues: {
      displayName: '', kind: 'salary', currency: 'EUR', payerLabel: '', note: '',
      owners: [{ personId: session.person.id, share: '100' }],
      phases: [newPhase(t('income_form.first_label'))],
    },
  });
  const lines = useFieldArray({ control: form.control, name: 'phases' });
  const errors = useSubmitErrors(form.setError, newIncomePath);
  const create = $api.useMutation('post', SOURCES, {
    onSuccess: ({ id }) => {
      void invalidateIncome(queryClient);
      void navigate({ to: '/income/$id', params: { id } });
    },
    onError: (error) => errors.onError(error),
  });
  const e = form.formState.errors;

  return (
    <article className="max-w-2xl space-y-4">
      <h1 className="text-2xl font-semibold">{t('income_form.title')}</h1>
      {errors.problem ? <ProblemMessage problem={errors.problem} /> : null}
      <form noValidate onSubmit={(ev) => void form.handleSubmit((values) => { errors.clear(); create.mutate({ body: toCreateBody(values) }); })(ev)} className="space-y-4">
        <Field label={t('income_form.name')} error={e.displayName?.message}>
          {(c) => <input {...c} {...form.register('displayName')} className={`w-full ${input}`} />}
        </Field>
        <Field label={t('income_form.kind')} error={e.kind?.message}>
          {(c) => (
            <select {...c} {...form.register('kind')} className={input}>
              {KINDS.map((k) => <option key={k} value={k}>{t(`income.kind.${k}`)}</option>)}
            </select>
          )}
        </Field>
        <Field label={t('income_form.currency')} error={e.currency?.message}>
          {(c) => <input maxLength={3} {...c} {...form.register('currency')} className={`w-20 uppercase ${input}`} />}
        </Field>
        <Field label={t('income_form.payer')} hint={t('income_form.payer_help')} error={e.payerLabel?.message}>
          {(c) => <input {...c} {...form.register('payerLabel')} className={`w-full ${input}`} />}
        </Field>
        <Field label={t('income_form.note')} error={e.note?.message}>
          {(c) => <textarea {...c} {...form.register('note')} className={`w-full ${input}`} />}
        </Field>
        <fieldset aria-describedby={e.visibility ? visibilityErrorId : undefined} className="space-y-1">
          <legend className="text-sm">{t('income_form.visibility')}</legend>
          {(['shared', 'private'] as const).map((v) => (
            <label key={v} className="flex items-center gap-2">
              <input type="radio" value={v} {...form.register('visibility')} />
              {t(`income_form.visibility_${v}`)}
            </label>
          ))}
          {e.visibility ? <p id={visibilityErrorId} className="text-sm text-destructive">{e.visibility.message}</p> : null}
        </fieldset>
        <OwnershipFields form={form} persons={people.data ?? []} withVisibility={false} />
        <section aria-labelledby={componentsId} className="space-y-3">
          <h2 id={componentsId} className="text-lg font-semibold">{t('income_form.components')}</h2>
          <p className="text-sm text-muted-foreground">{t('income_form.components_help')}</p>
          {lines.fields.map((row, i) => (
            <fieldset key={row.id} className="space-y-2 rounded-md border p-3">
              <legend className="px-1 text-sm font-medium">{t('income_form.component_n', { n: i + 1 })}</legend>
              <PhaseFields form={form} prefix={`phases.${i}.`} errors={e.phases?.[i]} />
              <Button type="button" variant="ghost" size="sm" onClick={() => { lines.remove(i); addRef.current?.focus(); }}>{t('income_form.remove_component', { n: i + 1 })}</Button>
            </fieldset>
          ))}
          <Button ref={addRef} type="button" variant="outline" size="sm" onClick={() => lines.append(newPhase())}>{t('income_form.add_component')}</Button>
        </section>
        <Button type="submit" disabled={create.isPending}>{t('income_form.submit')}</Button>
      </form>
    </article>
  );
}

import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { PlusIcon } from 'lucide-react';
import { useId, useRef, useState } from 'react';
import { Controller, useFieldArray, useForm, type Path } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { useSession } from '@/app/Providers';
import { CurrencyField } from '@/components/form/CurrencyField';
import { Field } from '@/components/form/Field';
import { Segmented } from '@/components/form/Segmented';
import { SelectField } from '@/components/form/SelectField';
import { OwnershipFields } from '@/components/ownership/OwnershipFields';
import { PageHeader } from '@/components/page/PageHeader';
import { StepForm } from '@/components/page/StepForm';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Textarea } from '@/components/ui/textarea';
import { Folded } from '../loans/new/Folded';
import { PERSONS } from '../subscriptions/api';
import { fieldErrorsOf } from '../subscriptions/fieldErrors';
import { useSubmitErrors } from '../subscriptions/edits/useSubmitErrors';
import { invalidateIncome, SOURCES } from './api';
import { KINDS } from './format';
import { newIncomePath, newIncomeSchema, toCreateBody, type NewIncomeValues } from './newIncomeSchema';
import { newPhase } from './phases/phase';
import { PhaseFields } from './phases/PhaseFields';
import '@/locales/income';
import '@/locales/loans';

/** The form's field paths by step: what « Continuer » validates, and where a server refusal sends the person back to. */
const FIELDS = {
  essentials: ['displayName', 'kind', 'payerLabel', 'visibility', 'owners', 'currency', 'note'],
  components: ['phases'],
} as const;

/** 2C2a design §5: a source, its visibility answered explicitly, and its components, in two steps and one request. */
export function NewIncomeSource() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { session } = useSession();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const people = $api.useQuery('get', PERSONS);
  const visibilityId = useId();
  const visibilityErrorId = useId();
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
  const [reveal, setReveal] = useState<{ step: string } | null>(null);
  const errors = useSubmitErrors(form.setError, newIncomePath);
  const create = $api.useMutation('post', SOURCES, {
    onSuccess: ({ id }) => {
      void invalidateIncome(queryClient);
      void navigate({ to: '/income/$id', params: { id } });
    },
    onError: (error) => {
      errors.onError(error);
      // The refusal lands on the last step: go back to the first step that holds a field it names.
      const roots = fieldErrorsOf(error).map((f) => errors.nameOf(f.path)?.split('.')[0]);
      const step = (Object.keys(FIELDS) as (keyof typeof FIELDS)[]).find((id) => roots.some((r) => (FIELDS[id] as readonly (string | undefined)[]).includes(r)));
      setReveal(step ? { step } : null);
    },
  });
  const e = form.formState.errors;

  const essentials = (
    <>
      <div className="grid gap-3 sm:grid-cols-3">
        <Field label={t('income_form.name')} error={e.displayName?.message}>
          {(c) => <Input {...c} {...form.register('displayName')} />}
        </Field>
        <Controller control={form.control} name="kind" render={({ field }) => (
          <Field label={t('income_form.kind')} error={e.kind?.message}>
            {(c) => <SelectField {...c} value={field.value} onValueChange={field.onChange}
              options={KINDS.map((k) => ({ value: k, label: t(`income.kind.${k}`) }))} />}
          </Field>
        )} />
        <Field label={t('income_form.payer')} hint={t('income_form.payer_help')} error={e.payerLabel?.message}>
          {(c) => <Input {...c} {...form.register('payerLabel')} />}
        </Field>
      </div>
      <div className="space-y-1">
        <span id={visibilityId} className="block text-sm">{t('income_form.visibility')}</span>
        <Controller control={form.control} name="visibility" render={({ field }) => (
          <Segmented aria-labelledby={visibilityId} aria-describedby={e.visibility ? visibilityErrorId : undefined}
            aria-invalid={e.visibility ? true : undefined} value={field.value ?? ''} onValueChange={field.onChange}
            options={(['shared', 'private'] as const).map((v) => ({ value: v, label: t(`income_form.visibility_${v}`) }))} />
        )} />
        {e.visibility ? <p id={visibilityErrorId} className="text-sm text-destructive">{e.visibility.message}</p> : null}
      </div>
      <OwnershipFields form={form} persons={people.data ?? []} withVisibility={false} />
      <Folded label={t('loan_form.more_options')} hasError={Boolean(e.currency || e.note)}>
        <div className="grid gap-3 sm:grid-cols-3">
          <Controller control={form.control} name="currency" render={({ field }) => (
            <Field label={t('income_form.currency')} error={e.currency?.message}>
              {(c) => <CurrencyField {...c} value={field.value} onValueChange={field.onChange} />}
            </Field>
          )} />
        </div>
        <Field label={t('income_form.note')} error={e.note?.message}>
          {(c) => <Textarea {...c} {...form.register('note')} />}
        </Field>
      </Folded>
    </>
  );

  const components = (
    <>
      <p className="text-sm text-muted-foreground">{t('income_form.components_help')}</p>
      {lines.fields.map((row, i) => (
        <fieldset key={row.id} className="space-y-3 rounded-md border p-3">
          <legend className="px-1 text-sm font-medium">{t('income_form.component_n', { n: i + 1 })}</legend>
          <PhaseFields form={form} prefix={`phases.${i}.`} errors={e.phases?.[i]} fold />
          <Button type="button" variant="ghost" size="sm" onClick={() => { lines.remove(i); addRef.current?.focus(); }}>{t('income_form.remove_component', { n: i + 1 })}</Button>
        </fieldset>
      ))}
      <Button ref={addRef} type="button" variant="outline" size="sm" onClick={() => lines.append(newPhase())}>
        <PlusIcon aria-hidden="true" />{t('income_form.add_component')}
      </Button>
    </>
  );

  return (
    <article className="max-w-3xl space-y-4">
      <PageHeader crumbs={[{ label: t('nav.income'), to: '/income' }]} title={t('income_form.title')} />
      {errors.problem ? <ProblemMessage problem={errors.problem} /> : null}
      <StepForm
        trigger={(fields) => form.trigger(fields as Path<NewIncomeValues>[])}
        onSubmit={() => void form.handleSubmit((values) => { errors.clear(); create.mutate({ body: toCreateBody(values) }); })()}
        submitLabel={t('income_form.submit')} pending={create.isPending} reveal={reveal}
        steps={[
          { id: 'essentials', title: t('income_form.step_essentials'), fields: [...FIELDS.essentials], content: essentials },
          { id: 'components', title: t('income_form.step_components'), fields: [...FIELDS.components], content: components },
        ]}
      />
    </article>
  );
}

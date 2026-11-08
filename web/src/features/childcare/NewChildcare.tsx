import { zodResolver } from '@hookform/resolvers/zod';
import { useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { Controller, useForm, type Path, type Resolver, type UseFormReturn } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { useSession } from '@/app/Providers';
import { Field } from '@/components/form/Field';
import { SelectField } from '@/components/form/SelectField';
import { OwnershipFields } from '@/components/ownership/OwnershipFields';
import { FormPage } from '@/components/page/FormPage';
import { StepForm } from '@/components/page/StepForm';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Input } from '@/components/ui/input';
import { Folded } from '@/components/form/Folded';
import { PERSONS } from '@/api/persons';
import { fieldErrorsOf } from '@/components/form/fieldErrors';
import { useSubmitErrors } from '@/components/form/useSubmitErrors';
import { useCreateChildcare } from './api';
import { childcarePath, childcareSchema, termsDefaults, toCreateBody, type ChildcareValues } from './childcareSchema';
import { GapFields } from './GapFields';
import { RateFields } from './RateFields';
import { WeekTypesFields } from './WeekTypesFields';
import '@/locales/childcare';

/**
 * The fields each step validates. The weeks of a single week type sit with the rates, those of several with their days: the lists
 * follow the count so that a step never blocks on an error shown in another one.
 */
const stepFields = (many: boolean) => ({
  who: ['name', 'childId', 'carerName', 'carerPajemploi', 'carerApproval'],
  rates: ['from', 'net', 'gross', 'overtimePct', 'upkeep', 'meal', ...(many ? [] : ['weekTypes.0.weeks'])],
  schedule: many ? ['weekTypes'] : ['weekTypes.0.days'],
  gaps: ['gaps'],
  owners: ['visibility', 'owners'],
});

/** The step that shows a form field (by its dotted name). */
function stepOf(name: string, many: boolean): string | undefined {
  if (name === 'weekTypes' || name.startsWith('weekTypes.')) return !many && name === 'weekTypes.0.weeks' ? 'rates' : 'schedule'; // the whole-array message shows with the days
  const root = name.split('.')[0]!;
  const steps = stepFields(many);
  return (Object.keys(steps) as (keyof typeof steps)[]).find((id) => (steps[id]).includes(root));
}

/** Spec §5: an arrangement in five steps and one request — who, rates, the weekly template, weeks without care, owners. */
export function NewChildcare() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { session } = useSession();
  const navigate = useNavigate();
  const people = $api.useQuery('get', PERSONS);
  const form = useForm<ChildcareValues>({
    resolver: zodResolver(childcareSchema(t)) as unknown as Resolver<ChildcareValues>,
    defaultValues: {
      ...termsDefaults(), name: '', childId: '', carerName: '', carerPajemploi: '', carerApproval: '', gaps: [],
      visibility: 'shared', owners: [{ personId: session.person.id, share: '100' }],
    },
  });
  const many = form.watch('weekTypes').length > 1;
  const steps = stepFields(many);
  const errors = useSubmitErrors(form.setError, (path) => childcarePath(path, form.getValues('weekTypes')),
    (f) => t(`childcare_form.error.${f.code.replaceAll('.', '_')}`, { defaultValue: f.message }));
  const [reveal, setReveal] = useState<{ step: string } | null>(null);
  const create = useCreateChildcare();

  const onError = (error: unknown) => {
    errors.onError(error);
    // The refusal lands on the last step: go back to the first step that holds a field it names.
    const step = fieldErrorsOf(error).map((f) => childcarePath(f.path, form.getValues('weekTypes'))).find(Boolean);
    setReveal(step ? { step: stepOf(step, many)! } : null);
  };

  return (
    <FormPage crumbs={[{ label: t('childcare_list.title'), to: '/childcare' }]} title={t('childcare_form.title')}>
      {errors.problem ? <ProblemMessage problem={errors.problem} /> : null}
      <StepForm
        trigger={(fields) => form.trigger(fields as Path<ChildcareValues>[])}
        onSubmit={() => void form.handleSubmit((values) => {
          errors.clear();
          create.mutate({ body: toCreateBody(values) }, {
            onSuccess: ({ id }) => void navigate({ to: '/childcare/$id', params: { id } }),
            onError,
          });
        }, (invalid) => setReveal({ step: stepOf(Object.keys(invalid).map((k) => (k === 'weekTypes' && !many && invalid.weekTypes?.[0]?.weeks ? 'weekTypes.0.weeks' : k))[0] ?? 'who', many) ?? 'who' }))()}
        submitLabel={t('childcare_form.submit')} pending={create.isPending} reveal={reveal}
        steps={[
          { id: 'who', title: t('childcare_form.step_who'), fields: steps.who, content: <WhoFields form={form} persons={people.data ?? []} /> },
          { id: 'rates', title: t('childcare_form.step_rates'), fields: steps.rates, content: <RateFields form={form} withFrom /> },
          { id: 'schedule', title: t('childcare_form.step_schedule'), fields: steps.schedule, content: <WeekTypesFields form={form} /> },
          { id: 'gaps', title: t('childcare_form.step_gaps'), fields: steps.gaps, content: <GapFields form={form} /> },
          { id: 'owners', title: t('childcare_form.step_owners'), fields: steps.owners, content: <OwnershipFields form={form} persons={people.data ?? []} /> },
        ]}
      />
    </FormPage>
  );
}

function WhoFields({ form, persons }: { form: UseFormReturn<ChildcareValues>; persons: { id: string; kind: string; displayName: string }[] }) {
  const { t } = useTranslation();
  const e = form.formState.errors;
  const children = persons.filter((p) => p.kind === 'dependent').map((p) => ({ value: p.id, label: p.displayName }));
  return (
    <>
      <Field label={t('childcare_form.name')} error={e.name?.message}>
        {(c) => <Input {...c} {...form.register('name')} />}
      </Field>
      <Controller control={form.control} name="childId" render={({ field }) => (
        <Field label={t('childcare_form.child')} hint={children.length === 0 ? t('childcare_form.no_child') : undefined} error={e.childId?.message}>
          {(c) => <SelectField {...c} value={field.value} onValueChange={field.onChange} options={children} />}
        </Field>
      )} />
      <Field label={t('childcare_form.carer')} error={e.carerName?.message}>
        {(c) => <Input {...c} {...form.register('carerName')} />}
      </Field>
      <Folded label={t('childcare_form.carer_more')} hasError={Boolean(e.carerPajemploi ?? e.carerApproval)}>
        <Field label={t('childcare_form.pajemploi')} error={e.carerPajemploi?.message}>
          {(c) => <Input {...c} {...form.register('carerPajemploi')} />}
        </Field>
        <Field label={t('childcare_form.approval')} error={e.carerApproval?.message}>
          {(c) => <Input {...c} {...form.register('carerApproval')} />}
        </Field>
      </Folded>
    </>
  );
}

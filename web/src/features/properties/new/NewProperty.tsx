import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useId, useState } from 'react';
import { Controller, FormProvider, useForm, type Path } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { useSession } from '@/app/Providers';
import { CurrencyField } from '@/components/form/CurrencyField';
import { Field } from '@/components/form/Field';
import { Checkbox } from '@/components/ui/checkbox';
import { Segmented } from '@/components/form/Segmented';
import { OwnershipFields } from '@/components/ownership/OwnershipFields';
import { FormPage } from '@/components/page/FormPage';
import { StepForm } from '@/components/page/StepForm';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Input } from '@/components/ui/input';
import { Textarea } from '@/components/ui/textarea';
import { Folded } from '@/components/form/Folded';
import { PERSONS } from '@/api/persons';
import { fieldErrorsOf } from '@/components/form/fieldErrors';
import { useSubmitErrors } from '@/components/form/useSubmitErrors';
import { invalidateProperties, PROPERTIES } from '../api';
import { newPropertyPath, newPropertySchema, toCreateBody, type NewPropertyValues } from './propertyForm';
import { IdentityFields, PurchaseFields } from './PropertyFields';
import '@/locales/loans';

/** The form's field paths by step: what « Continuer » validates, and where a server refusal sends the person back to. */
const FIELDS = {
  essentials: ['name', 'type', 'locationLabel', 'visibility', 'owners', 'currency', 'note'],
  purchase: ['purchaseDate', 'purchasePrice', 'notaryFees', 'agencyFees', 'otherAcquisitionCosts', 'surfaceM2', 'valuationCadence', 'staleAfterDays'],
} as const;

/** 3D1 design §5: a property, its visibility answered explicitly, in two steps and one request; no estimate is asked here. */
export function NewProperty() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { session } = useSession();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const people = $api.useQuery('get', PERSONS);
  const visibilityId = useId();
  const visibilityErrorId = useId();
  const form = useForm<NewPropertyValues>({
    resolver: zodResolver(newPropertySchema(t)),
    // `visibility` is left out on purpose: neither answer is checked until the person picks one.
    defaultValues: {
      name: '', currency: 'EUR', note: '', owners: [{ personId: session.person.id, share: '100' }],
      type: 'primary_residence', purchaseDate: '', purchasePrice: '', notaryFees: '0', agencyFees: '0', otherAcquisitionCosts: '0',
      surfaceM2: '', locationLabel: '', valuationCadence: 'yearly', staleAfterDays: '', withLoan: false,
    },
  });
  const [reveal, setReveal] = useState<{ step: string } | null>(null);
  const errors = useSubmitErrors(form.setError, newPropertyPath);
  const create = $api.useMutation('post', PROPERTIES, {
    onSuccess: ({ id }) => {
      void invalidateProperties(queryClient);
      void (form.getValues('withLoan')
        ? navigate({ to: '/loans/new', search: { property: id } })
        : navigate({ to: '/properties/$id', params: { id } }));
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
      <Field label={t('property_form.name')} error={e.name?.message}>
        {(c) => <Input {...c} {...form.register('name')} />}
      </Field>
      <IdentityFields />
      <div className="space-y-1">
        <span id={visibilityId} className="block text-sm">{t('property_form.visibility')}</span>
        <Controller control={form.control} name="visibility" render={({ field }) => (
          <Segmented aria-labelledby={visibilityId} aria-describedby={e.visibility ? visibilityErrorId : undefined}
            aria-invalid={e.visibility ? true : undefined} value={field.value ?? ''} onValueChange={field.onChange}
            options={(['shared', 'private'] as const).map((v) => ({ value: v, label: t(`loan_form.visibility_${v}`) }))} />
        )} />
        {e.visibility ? <p id={visibilityErrorId} className="text-sm text-destructive">{e.visibility.message}</p> : null}
      </div>
      <OwnershipFields form={form} persons={people.data ?? []} withVisibility={false} />
      <Folded label={t('loan_form.more_options')} hasError={Boolean(e.currency || e.note)}>
        <div className="grid gap-3 sm:grid-cols-2">
          <Controller control={form.control} name="currency" render={({ field }) => (
            <Field label={t('property_form.currency')} error={e.currency?.message}>
              {(c) => <CurrencyField {...c} value={field.value} onValueChange={field.onChange} />}
            </Field>
          )} />
        </div>
        <Field label={t('property_form.note')} error={e.note?.message}>
          {(c) => <Textarea {...c} {...form.register('note')} />}
        </Field>
      </Folded>
    </>
  );

  return (
    <FormPage crumbs={[{ label: t('nav.properties'), to: '/properties' }]} title={t('property_form.title')}>
      {errors.problem ? <ProblemMessage problem={errors.problem} /> : null}
      {/* The property's fields are shared with the settings (3D1 design §6): they read this form from the context. */}
      <FormProvider {...form}>
        <StepForm
          trigger={(fields) => form.trigger(fields as Path<NewPropertyValues>[])}
          onSubmit={() => void form.handleSubmit((values) => { errors.clear(); create.mutate({ body: toCreateBody(values) }); })()}
          submitLabel={t('property_form.submit')} pending={create.isPending} reveal={reveal}
          steps={[
            { id: 'essentials', title: t('property_form.step_essentials'), fields: [...FIELDS.essentials], content: essentials },
            {
              id: 'purchase', title: t('property_form.step_purchase'), fields: [...FIELDS.purchase],
              content: (
                <>
                  <PurchaseFields />
                  <Controller control={form.control} name="withLoan" render={({ field }) => (
                    <Field inline label={t('property_form.with_loan')} hint={field.value ? t('property_form.with_loan_hint') : undefined}>
                      {(c) => <Checkbox {...c} checked={field.value} onCheckedChange={(v) => field.onChange(v === true)} />}
                    </Field>
                  )} />
                </>
              ),
            },
          ]}
        />
      </FormProvider>
    </FormPage>
  );
}

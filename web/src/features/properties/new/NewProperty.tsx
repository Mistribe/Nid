import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useId } from 'react';
import { FormProvider, useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { useSession } from '@/app/Providers';
import { OwnershipFields } from '@/components/ownership/OwnershipFields';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { PERSONS } from '../../subscriptions/api';
import { Field } from '../../subscriptions/edits/Field';
import { useSubmitErrors } from '../../subscriptions/edits/useSubmitErrors';
import { invalidateProperties, PROPERTIES } from '../api';
import { newPropertyPath, newPropertySchema, toCreateBody, type NewPropertyValues } from './propertyForm';
import { PropertyFields } from './PropertyFields';

const input = 'rounded-md border px-3 py-2';

/** 3D1 design §5: a property, its visibility answered explicitly, in one request; no estimate is asked here. */
export function NewProperty() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { session } = useSession();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const people = $api.useQuery('get', PERSONS);
  const visibilityErrorId = useId();
  const propertyId = useId();
  const form = useForm<NewPropertyValues>({
    resolver: zodResolver(newPropertySchema(t)),
    // `visibility` is left out on purpose: neither answer is checked until the person picks one.
    defaultValues: {
      name: '', currency: 'EUR', note: '', owners: [{ personId: session.person.id, share: '100' }],
      type: 'primary_residence', purchaseDate: '', purchasePrice: '', notaryFees: '0', agencyFees: '0', otherAcquisitionCosts: '0',
      surfaceM2: '', locationLabel: '', valuationCadence: 'yearly', staleAfterDays: '',
    },
  });
  const errors = useSubmitErrors(form.setError, newPropertyPath);
  const create = $api.useMutation('post', PROPERTIES, {
    onSuccess: ({ id }) => {
      void invalidateProperties(queryClient);
      void navigate({ to: '/properties/$id', params: { id } });
    },
    onError: (error) => errors.onError(error),
  });
  const e = form.formState.errors;

  return (
    <article className="max-w-2xl space-y-4">
      <h1 className="text-2xl font-semibold">{t('property_form.title')}</h1>
      {errors.problem ? <ProblemMessage problem={errors.problem} /> : null}
      {/* The property's fields are shared with the settings (3D1 design §6): they read this form from the context. */}
      <FormProvider {...form}>
        <form noValidate onSubmit={(ev) => void form.handleSubmit((values) => { errors.clear(); create.mutate({ body: toCreateBody(values) }); })(ev)} className="space-y-4">
          <Field label={t('property_form.name')} error={e.name?.message}>
            {(c) => <input {...c} {...form.register('name')} className={`w-full ${input}`} />}
          </Field>
          <Field label={t('property_form.currency')} error={e.currency?.message}>
            {(c) => <input maxLength={3} {...c} {...form.register('currency')} className={`w-20 uppercase ${input}`} />}
          </Field>
          <Field label={t('property_form.note')} error={e.note?.message}>
            {(c) => <textarea {...c} {...form.register('note')} className={`w-full ${input}`} />}
          </Field>
          <fieldset aria-describedby={e.visibility ? visibilityErrorId : undefined} className="space-y-1">
            <legend className="text-sm">{t('property_form.visibility')}</legend>
            {(['shared', 'private'] as const).map((v) => (
              <label key={v} className="flex items-center gap-2">
                <input type="radio" value={v} {...form.register('visibility')} />
                {t(`loan_form.visibility_${v}`)}
              </label>
            ))}
            {e.visibility ? <p id={visibilityErrorId} className="text-sm text-destructive">{e.visibility.message}</p> : null}
          </fieldset>
          <OwnershipFields form={form} persons={people.data ?? []} withVisibility={false} />
          <section aria-labelledby={propertyId} className="space-y-3">
            <h2 id={propertyId} className="text-lg font-semibold">{t('property_form.the_property')}</h2>
            <PropertyFields />
          </section>
          <Button type="submit" disabled={create.isPending}>{t('property_form.submit')}</Button>
        </form>
      </FormProvider>
    </article>
  );
}

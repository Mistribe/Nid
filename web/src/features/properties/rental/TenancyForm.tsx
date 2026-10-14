import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useId, useState } from 'react';
import { useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { presentRefusal } from '../../loans/refusal';
import { Field } from '../../subscriptions/edits/Field';
import { Reload } from '../../subscriptions/edits/Reload';
import { useSubmitErrors } from '../../subscriptions/edits/useSubmitErrors';
import { invalidateProperties, TENANCIES, TENANCY, type PropertyDetail, type PropertyTenancy } from '../api';
import { TENANCY_PATHS, tenancySchema, tenancyValuesOf, toTenancyBody, type TenancyValues } from './tenancy';

const input = 'rounded-md border px-3 py-2';

/**
 * Spec 3D2 §3: a tenancy added (`editing` absent: POST) or corrected inline on its row (PUT, with the version read) —
 * ending a tenancy is correcting it with an end. The section announces and places the focus: this form is gone by then.
 */
export function TenancyForm({ property, editing, onDone }: { property: PropertyDetail; editing?: PropertyTenancy; onDone: (message?: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const headingId = useId();
  // The version the form opened on: a refetch meanwhile must not turn the expected 409 into an overwrite.
  const [version] = useState(editing?.version);
  const form = useForm<TenancyValues>({ resolver: zodResolver(tenancySchema(t)), defaultValues: tenancyValuesOf(editing) });
  const errors = useSubmitErrors(form.setError, TENANCY_PATHS);
  // Mount only: adding lands on the label, correcting on the end (the usual correction is ending it).
  useEffect(() => { form.setFocus(editing ? 'endsOn' : 'label'); }, []); // eslint-disable-line react-hooks/exhaustive-deps
  const add = $api.useMutation('post', TENANCIES);
  const correct = $api.useMutation('put', TENANCY);

  const submit = form.handleSubmit((values) => {
    errors.clear();
    const callbacks = {
      onSuccess: () => {
        void invalidateProperties(queryClient);
        onDone(editing ? t('property_rental.corrected') : t('property_rental.added'));
      },
      onError: (error: unknown) => errors.onError(error),
    };
    if (editing) correct.mutate({ params: { path: { id: property.id, tenancyId: editing.id } }, body: toTenancyBody(values, version) }, callbacks);
    else add.mutate({ params: { path: { id: property.id } }, body: toTenancyBody(values) }, callbacks);
  });

  const e = form.formState.errors;
  const problem = errors.problem ? presentRefusal(t, editing ? correct.error : add.error) : null;
  const amount = (name: 'rentMonthly' | 'chargesMonthly' | 'deposit', label: string, hint?: string) => (
    <Field label={label} hint={hint} error={e[name]?.message}>
      {(c) => <input inputMode="decimal" {...c} {...form.register(name)} className={`w-32 ${input}`} />}
    </Field>
  );

  return (
    <form aria-labelledby={headingId} noValidate onSubmit={(ev) => void submit(ev)} className="max-w-md space-y-3">
      <h3 id={headingId} className="font-semibold">{editing ? t('property_rental.edit_title') : t('property_rental.add_title')}</h3>
      {problem ? (
        <>
          <ProblemMessage problem={problem} />
          {/* The row changed or went: reading it again closes the form onto what is now stored. */}
          {problem.kind === 'conflict' || problem.kind === 'not_found'
            ? <Reload onReload={() => invalidateProperties(queryClient).then(() => onDone())} />
            : null}
        </>
      ) : null}
      <Field label={t('property_rental.label')} hint={t('property_rental.label_help')} error={e.label?.message}>
        {(c) => <input {...c} {...form.register('label')} className={`w-full ${input}`} />}
      </Field>
      <Field label={t('property_rental.starts')} error={e.startsOn?.message}>
        {(c) => <input type="date" {...c} {...form.register('startsOn')} className={input} />}
      </Field>
      <Field label={t('property_rental.ends')} hint={t('loan_form.optional')} error={e.endsOn?.message}>
        {(c) => <input type="date" {...c} {...form.register('endsOn')} className={input} />}
      </Field>
      {amount('rentMonthly', t('property_rental.rent'))}
      {amount('chargesMonthly', t('property_rental.charges'))}
      {amount('deposit', t('property_rental.deposit'), t('loan_form.optional'))}
      <Field label={t('property_rental.note')} error={e.note?.message}>
        {(c) => <textarea {...c} {...form.register('note')} className={`w-full ${input}`} />}
      </Field>
      <div className="flex items-center gap-2">
        <Button type="submit" disabled={add.isPending || correct.isPending}>{editing ? t('loan_detail.save') : t('property_rental.submit')}</Button>
        <Button type="button" variant="ghost" onClick={() => onDone()}>{t('loan_detail.cancel')}</Button>
      </div>
    </form>
  );
}

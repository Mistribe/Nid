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
import { todayIso } from '../../subscriptions/today';
import { invalidateProperties } from '../../properties/api';
import { invalidateLoans, LOANS } from '../api';
import { PropertySelect } from '../PropertySelect';
import { ContractFields } from './ContractFields';
import { newLoanPath, newLoanSchema, newPhase, plusOneMonth, toCreateBody, type NewLoanValues } from './loanForm';

const input = 'rounded-md border px-3 py-2';

/** 3B1 design §6: a loan, its visibility answered explicitly, plain by default, in one request. */
export function NewLoan() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { session } = useSession();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const people = $api.useQuery('get', PERSONS);
  const visibilityErrorId = useId();
  const today = todayIso();
  const form = useForm<NewLoanValues>({
    resolver: zodResolver(newLoanSchema(t)),
    // `visibility` is left out on purpose: neither answer is checked until the person picks one.
    defaultValues: {
      name: '', lender: '', currency: 'EUR', note: '', owners: [{ personId: session.person.id, share: '100' }],
      principal: '', totalPayments: '', disbursedOn: today, firstPaymentDate: plusOneMonth(today),
      rounding: 'half_up', firstPeriod: 'full', phases: [newPhase()], insurances: [], propertyId: '',
    },
  });
  const errors = useSubmitErrors(form.setError, newLoanPath);
  const create = $api.useMutation('post', LOANS, {
    onSuccess: ({ id }) => {
      void invalidateLoans(queryClient);
      void invalidateProperties(queryClient); // a linked property's detail now lists this loan (3D1 design §7)
      void navigate({ to: '/loans/$id', params: { id } });
    },
    onError: (error) => errors.onError(error),
  });
  const e = form.formState.errors;

  return (
    <article className="max-w-2xl space-y-4">
      <h1 className="text-2xl font-semibold">{t('loan_form.title')}</h1>
      {errors.problem ? <ProblemMessage problem={errors.problem} /> : null}
      {/* The contract's fields are shared with the contract page (3B2 design §5): they read this form from the context. */}
      <FormProvider {...form}>
        <form noValidate onSubmit={(ev) => void form.handleSubmit((values) => { errors.clear(); create.mutate({ body: toCreateBody(values) }); })(ev)} className="space-y-4">
          <Field label={t('loan_form.name')} error={e.name?.message}>
            {(c) => <input {...c} {...form.register('name')} className={`w-full ${input}`} />}
          </Field>
          <Field label={t('loan_form.lender')} hint={t('loan_form.lender_help')} error={e.lender?.message}>
            {(c) => <input {...c} {...form.register('lender')} className={`w-full ${input}`} />}
          </Field>
          <Field label={t('loan_form.currency')} error={e.currency?.message}>
            {(c) => <input maxLength={3} {...c} {...form.register('currency')} className={`w-20 uppercase ${input}`} />}
          </Field>
          <Field label={t('loan_form.note')} error={e.note?.message}>
            {(c) => <textarea {...c} {...form.register('note')} className={`w-full ${input}`} />}
          </Field>
          <fieldset aria-describedby={e.visibility ? visibilityErrorId : undefined} className="space-y-1">
            <legend className="text-sm">{t('loan_form.visibility')}</legend>
            {(['shared', 'private'] as const).map((v) => (
              <label key={v} className="flex items-center gap-2">
                <input type="radio" value={v} {...form.register('visibility')} />
                {t(`loan_form.visibility_${v}`)}
              </label>
            ))}
            {e.visibility ? <p id={visibilityErrorId} className="text-sm text-destructive">{e.visibility.message}</p> : null}
          </fieldset>
          <OwnershipFields form={form} persons={people.data ?? []} withVisibility={false} />
          <PropertySelect registration={form.register('propertyId')} error={e.propertyId?.message} />
          <ContractFields />
          <Button type="submit" disabled={create.isPending}>{t('loan_form.submit')}</Button>
        </form>
      </FormProvider>
    </article>
  );
}

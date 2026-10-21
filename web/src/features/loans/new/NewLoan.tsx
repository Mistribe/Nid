import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { useNavigate, useSearch } from '@tanstack/react-router';
import type { NewLoanSearch } from '@/app/router';
import { useId, useState } from 'react';
import { Controller, useForm, type Path } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { useSession } from '@/app/Providers';
import { CurrencyField } from '@/components/form/CurrencyField';
import { Field } from '@/components/form/Field';
import { Segmented } from '@/components/form/Segmented';
import { OwnershipFields } from '@/components/ownership/OwnershipFields';
import { PageHeader } from '@/components/page/PageHeader';
import { StepForm } from '@/components/page/StepForm';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Input } from '@/components/ui/input';
import { Textarea } from '@/components/ui/textarea';
import { PERSONS } from '../../subscriptions/api';
import { fieldErrorsOf } from '../../subscriptions/fieldErrors';
import { useSubmitErrors } from '../../subscriptions/edits/useSubmitErrors';
import { todayIso } from '../../subscriptions/today';
import { invalidateProperties } from '../../properties/api';
import { invalidateLoans, LOANS } from '../api';
import { PropertySelect } from '../PropertySelect';
import { InsuranceSection, LoanFields, useContract } from './ContractFields';
import { Folded } from './Folded';
import { contractFormOf, newLoanPath, newLoanSchema, newPhase, plusOneMonth, toCreateBody, type NewLoanValues } from './loanForm';
import '@/locales/loans';

/** The form's field paths by step: what « Continuer » validates, and where a server refusal sends the person back to. */
const FIELDS = {
  essentials: ['name', 'lender', 'currency', 'note', 'visibility', 'owners'],
  loan: ['principal', 'totalPayments', 'disbursedOn', 'firstPaymentDate', 'phases', 'rounding', 'firstPeriod', 'propertyId'],
  insurance: ['insurances'],
} as const;

/** 3B1 design §6: a loan in three steps, its visibility answered explicitly, plain by default, in one request. */
export function NewLoan() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { session } = useSession();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const search: NewLoanSearch = useSearch({ strict: false });
  const people = $api.useQuery('get', PERSONS);
  const visibilityId = useId();
  const visibilityErrorId = useId();
  const today = todayIso();
  const form = useForm<NewLoanValues>({
    resolver: zodResolver(newLoanSchema(t)),
    // `visibility` is left out on purpose: neither answer is checked until the person picks one.
    defaultValues: {
      name: '', lender: '', currency: 'EUR', note: '', owners: [{ personId: session.person.id, share: '100' }],
      principal: '', totalPayments: '', disbursedOn: today, firstPaymentDate: plusOneMonth(today),
      rounding: 'half_up', firstPeriod: 'full', phases: [newPhase()], insurances: [], propertyId: search.property ?? '',
    },
  });
  // The contract's state lives here, not in its fields: a step unmounts them. Its fields are a subset of this form's.
  const contract = useContract(contractFormOf(form));
  const [reveal, setReveal] = useState<{ step: string } | null>(null);
  const errors = useSubmitErrors(form.setError, newLoanPath);
  const create = $api.useMutation('post', LOANS, {
    onSuccess: ({ id }) => {
      void invalidateLoans(queryClient);
      void invalidateProperties(queryClient); // a linked property's detail now lists this loan (3D1 design §7)
      void navigate({ to: '/loans/$id', params: { id } });
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
      <div className="grid gap-3 sm:grid-cols-2">
        <Field label={t('loan_form.name')} error={e.name?.message}>
          {(c) => <Input {...c} {...form.register('name')} />}
        </Field>
        <Field label={t('loan_form.lender')} hint={t('loan_form.lender_help')} error={e.lender?.message}>
          {(c) => <Input {...c} {...form.register('lender')} />}
        </Field>
      </div>
      <div className="space-y-1">
        <span id={visibilityId} className="block text-sm">{t('loan_form.visibility')}</span>
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
            <Field label={t('loan_form.currency')} error={e.currency?.message}>
              {(c) => <CurrencyField {...c} value={field.value} onValueChange={field.onChange} />}
            </Field>
          )} />
        </div>
        <Field label={t('loan_form.note')} error={e.note?.message}>
          {(c) => <Textarea {...c} {...form.register('note')} />}
        </Field>
      </Folded>
    </>
  );

  return (
    <article className="max-w-3xl space-y-4">
      <PageHeader crumbs={[{ label: t('nav.loans'), to: '/loans' }]} title={t('loan_form.title')} />
      {errors.problem ? <ProblemMessage problem={errors.problem} /> : null}
      <StepForm
        trigger={(fields) => form.trigger(fields as Path<NewLoanValues>[])}
        onSubmit={() => void form.handleSubmit((values) => { errors.clear(); create.mutate({ body: toCreateBody(values) }); })()}
        submitLabel={t('loan_form.submit')} pending={create.isPending} reveal={reveal}
        steps={[
          { id: 'essentials', title: t('loan_form.step_essentials'), fields: [...FIELDS.essentials], content: essentials },
          {
            id: 'loan', title: t('loan_form.step_loan'), fields: [...FIELDS.loan],
            content: (
              <LoanFields contract={contract}>
                <Controller control={form.control} name="propertyId" render={({ field }) => (
                  <PropertySelect value={field.value} onValueChange={field.onChange} error={e.propertyId?.message} />
                )} />
              </LoanFields>
            ),
          },
          { id: 'insurance', title: t('loan_form.step_insurance'), fields: [...FIELDS.insurance], content: <InsuranceSection contract={contract} /> },
        ]}
      />
    </article>
  );
}

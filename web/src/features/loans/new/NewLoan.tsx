import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useId, useRef, type ChangeEvent } from 'react';
import { useFieldArray, useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { useSession } from '@/app/Providers';
import { OwnershipFields } from '@/components/ownership/OwnershipFields';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { PERSONS } from '../../subscriptions/api';
import { ISO_DATE } from '../../subscriptions/edits/amount';
import { Field } from '../../subscriptions/edits/Field';
import { useSubmitErrors } from '../../subscriptions/edits/useSubmitErrors';
import { todayIso } from '../../subscriptions/today';
import { invalidateLoans, LOANS } from '../api';
import { FIRST_PERIODS, ROUNDINGS } from '../format';
import { Folded } from './Folded';
import { InsuranceFields } from './InsuranceFields';
import {
  MAX_INSURANCES, newInsurance, newLoanPath, newLoanSchema, newPhase, PLAIN_HIDDEN, plusOneMonth, toCreateBody, type NewLoanValues,
} from './newLoan';
import { PhaseFields } from './PhaseFields';

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
  const termsId = useId();
  const phasesId = useId();
  const insurancesId = useId();
  const addPhaseRef = useRef<HTMLButtonElement>(null);
  const addInsuranceRef = useRef<HTMLButtonElement>(null);
  // The first payment follows the disbursement until the person types in it (precision 12).
  const firstTouched = useRef(false);
  const today = todayIso();
  const form = useForm<NewLoanValues>({
    resolver: zodResolver(newLoanSchema(t)),
    // `visibility` is left out on purpose: neither answer is checked until the person picks one.
    defaultValues: {
      name: '', lender: '', currency: 'EUR', note: '', owners: [{ personId: session.person.id, share: '100' }],
      principal: '', totalPayments: '', disbursedOn: today, firstPaymentDate: plusOneMonth(today),
      rounding: 'half_up', firstPeriod: 'full', phases: [newPhase()], insurances: [],
    },
  });
  const phases = useFieldArray({ control: form.control, name: 'phases' });
  const insurances = useFieldArray({ control: form.control, name: 'insurances' });
  const errors = useSubmitErrors(form.setError, newLoanPath);
  const create = $api.useMutation('post', LOANS, {
    onSuccess: ({ id }) => {
      void invalidateLoans(queryClient);
      void navigate({ to: '/loans/$id', params: { id } });
    },
    onError: (error) => errors.onError(error),
  });
  const e = form.formState.errors;
  const plain = phases.fields.length === 1;

  const removePhase = (i: number) => {
    phases.remove(i);
    // Back to a plain loan (precision 11): the line left keeps its mode, rate and type; what a plain loan hides is cleared.
    if (phases.fields.length === 2) for (const field of PLAIN_HIDDEN) form.setValue(`phases.0.${field}`, '');
    addPhaseRef.current?.focus();
  };
  const removeInsurance = (i: number) => {
    insurances.remove(i);
    addInsuranceRef.current?.focus();
  };
  const followDisbursement = (ev: ChangeEvent<HTMLInputElement>) => {
    if (!firstTouched.current && ISO_DATE.test(ev.target.value)) form.setValue('firstPaymentDate', plusOneMonth(ev.target.value));
  };

  return (
    <article className="max-w-2xl space-y-4">
      <h1 className="text-2xl font-semibold">{t('loan_form.title')}</h1>
      {errors.problem ? <ProblemMessage problem={errors.problem} /> : null}
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

        <section aria-labelledby={termsId} className="space-y-3">
          <h2 id={termsId} className="text-lg font-semibold">{t('loan_form.terms')}</h2>
          <Field label={t('loan_form.principal')} error={e.principal?.message}>
            {(c) => <input inputMode="decimal" {...c} {...form.register('principal')} className={`w-40 ${input}`} />}
          </Field>
          {plain ? (
            <Field label={t('loan_form.rate')} error={e.phases?.[0]?.rate?.message}>
              {(c) => <input inputMode="decimal" {...c} {...form.register('phases.0.rate')} className={`w-24 ${input}`} />}
            </Field>
          ) : null}
          <Field label={t('loan_form.duration')} error={e.totalPayments?.message}>
            {(c) => <input inputMode="numeric" {...c} {...form.register('totalPayments')} className={`w-24 ${input}`} />}
          </Field>
          <Field label={t('loan_form.disbursed_on')} error={e.disbursedOn?.message}>
            {(c) => <input type="date" {...c} {...form.register('disbursedOn', { onChange: followDisbursement })} className={input} />}
          </Field>
          <Field label={t('loan_form.first_payment')} hint={t('loan_form.first_payment_help')} error={e.firstPaymentDate?.message}>
            {(c) => <input type="date" {...c} {...form.register('firstPaymentDate', { onChange: () => { firstTouched.current = true; } })} className={input} />}
          </Field>
        </section>

        {plain ? null : (
          <section aria-labelledby={phasesId} className="space-y-3">
            <h2 id={phasesId} className="text-lg font-semibold">{t('loan_form.phases')}</h2>
            <p className="text-sm text-muted-foreground">{t('loan_form.phases_help')}</p>
            {phases.fields.map((row, i) => (
              <fieldset key={row.id} className="space-y-2 rounded-md border p-3">
                <legend className="px-1 text-sm font-medium">{t('loan_form.phase_n', { n: i + 1 })}</legend>
                <PhaseFields form={form} index={i} last={i === phases.fields.length - 1} />
                <Button type="button" variant="ghost" size="sm" onClick={() => removePhase(i)}>{t('loan_form.remove_phase', { n: i + 1 })}</Button>
              </fieldset>
            ))}
          </section>
        )}
        <Button ref={addPhaseRef} type="button" variant="outline" size="sm" onClick={() => phases.append(newPhase())}>{t('loan_form.add_phase')}</Button>

        <Folded label={t('loan_form.options')} hasError={Boolean(e.rounding || e.firstPeriod)}>
          <Field label={t('loan_form.rounding')} error={e.rounding?.message}>
            {(c) => (
              <select {...c} {...form.register('rounding')} className={input}>
                {ROUNDINGS.map((r) => <option key={r} value={r}>{t(`loan_form.rounding_option.${r}`)}</option>)}
              </select>
            )}
          </Field>
          <Field label={t('loan_form.first_period')} error={e.firstPeriod?.message}>
            {(c) => (
              <select {...c} {...form.register('firstPeriod')} className={input}>
                {FIRST_PERIODS.map((p) => <option key={p} value={p}>{t(`loan_form.first_period_option.${p}`)}</option>)}
              </select>
            )}
          </Field>
        </Folded>

        <section aria-labelledby={insurancesId} className="space-y-3">
          <h2 id={insurancesId} className="text-lg font-semibold">{t('loan_form.insurances')}</h2>
          {insurances.fields.map((row, i) => (
            <fieldset key={row.id} className="space-y-2 rounded-md border p-3">
              <legend className="px-1 text-sm font-medium">{t('loan_form.insurance_n', { n: i + 1 })}</legend>
              <InsuranceFields form={form} index={i} />
              <Button type="button" variant="ghost" size="sm" onClick={() => removeInsurance(i)}>{t('loan_form.remove_insurance', { n: i + 1 })}</Button>
            </fieldset>
          ))}
          {insurances.fields.length < MAX_INSURANCES ? (
            <Button ref={addInsuranceRef} type="button" variant="outline" size="sm"
              onClick={() => insurances.append(newInsurance(form.getValues('firstPaymentDate')))}>{t('loan_form.add_insurance')}</Button>
          ) : null}
        </section>

        <Button type="submit" disabled={create.isPending}>{t('loan_form.submit')}</Button>
      </form>
    </article>
  );
}

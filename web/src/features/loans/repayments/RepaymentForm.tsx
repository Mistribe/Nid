import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useId, useState } from 'react';
import { useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Field } from '../../subscriptions/edits/Field';
import { Reload } from '../../subscriptions/edits/Reload';
import { useSubmitErrors } from '../../subscriptions/edits/useSubmitErrors';
import { invalidateLoans, REPAYMENT, REPAYMENTS, type LoanDetail, type LoanRepayment } from '../api';
import { EFFECTS } from '../format';
import { presentRefusal } from '../refusal';
import { REPAYMENT_PATHS, repaymentSchema, repaymentValuesOf, toRepaymentBody, type RepaymentValues } from './repayment';

const input = 'rounded-md border px-3 py-2';

/**
 * 3B2 design §7: adding an early repayment (`editing` absent: POST) or correcting it inline (PUT, with the version read);
 * its penalty is typed here, never computed. The section announces and places the focus.
 */
export function RepaymentForm({ loan, editing, onDone }: { loan: LoanDetail; editing?: LoanRepayment; onDone: (message?: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const headingId = useId();
  // The version the form opened on: a refetch meanwhile must not turn the expected 409 into an overwrite.
  const [version] = useState(editing?.version);
  const effectErrorId = useId();
  const form = useForm<RepaymentValues>({ resolver: zodResolver(repaymentSchema(t)), defaultValues: repaymentValuesOf(editing) });
  const errors = useSubmitErrors(form.setError, REPAYMENT_PATHS);
  // Mount only: opening the form lands the person in it (the date when adding, the amount when correcting).
  useEffect(() => { form.setFocus(editing ? 'amount' : 'onDate'); }, []); // eslint-disable-line react-hooks/exhaustive-deps
  const add = $api.useMutation('post', REPAYMENTS);
  const correct = $api.useMutation('put', REPAYMENT);

  const submit = form.handleSubmit((values) => {
    errors.clear();
    const callbacks = {
      onSuccess: () => {
        void invalidateLoans(queryClient);
        onDone(editing ? t('loan_repayments.corrected') : t('loan_repayments.added'));
      },
      onError: (error: unknown) => errors.onError(error),
    };
    if (editing) correct.mutate({ params: { path: { id: loan.id, eventId: editing.id } }, body: toRepaymentBody(values, version) }, callbacks);
    else add.mutate({ params: { path: { id: loan.id } }, body: toRepaymentBody(values) }, callbacks);
  });

  const e = form.formState.errors;
  const problem = errors.problem ? presentRefusal(t, editing ? correct.error : add.error) : null;

  return (
    <form aria-labelledby={headingId} noValidate onSubmit={(ev) => void submit(ev)} className="max-w-md space-y-3">
      <h3 id={headingId} className="font-semibold">{editing ? t('loan_repayments.edit_title') : t('loan_repayments.add_title')}</h3>
      {problem ? (
        <>
          <ProblemMessage problem={problem} />
          {/* The row changed or went: reading it again closes the form onto what is now stored. */}
          {problem.kind === 'conflict' || problem.kind === 'not_found' ? <Reload onReload={() => invalidateLoans(queryClient).then(() => onDone())} /> : null}
        </>
      ) : null}
      <Field label={t('loan_repayments.date')} error={e.onDate?.message}>
        {(c) => <input type="date" {...c} {...form.register('onDate')} className={input} />}
      </Field>
      <Field label={t('loan_repayments.amount')} error={e.amount?.message}>
        {(c) => <input inputMode="decimal" {...c} {...form.register('amount')} className={`w-32 ${input}`} />}
      </Field>
      <fieldset aria-describedby={e.effect ? effectErrorId : undefined} className="space-y-1">
        <legend className="text-sm">{t('loan_repayments.effect')}</legend>
        {EFFECTS.map((v) => (
          <label key={v} className="flex items-center gap-2">
            <input type="radio" value={v} {...form.register('effect')} />
            {t(`loan_repayments.effect_option.${v}`)}
          </label>
        ))}
        {e.effect ? <p id={effectErrorId} className="text-sm text-destructive">{e.effect.message}</p> : null}
      </fieldset>
      <Field label={t('loan_repayments.penalty')} hint={t('loan_repayments.penalty_help')} error={e.penalty?.message}>
        {(c) => <input inputMode="decimal" {...c} {...form.register('penalty')} className={`w-32 ${input}`} />}
      </Field>
      <Field label={t('loan_repayments.note')} error={e.note?.message}>
        {(c) => <textarea {...c} {...form.register('note')} className={`w-full ${input}`} />}
      </Field>
      <div className="flex items-center gap-2">
        <Button type="submit" disabled={add.isPending || correct.isPending}>{editing ? t('loan_detail.save') : t('loan_repayments.submit')}</Button>
        <Button type="button" variant="ghost" onClick={() => onDone()}>{t('loan_detail.cancel')}</Button>
      </div>
    </form>
  );
}

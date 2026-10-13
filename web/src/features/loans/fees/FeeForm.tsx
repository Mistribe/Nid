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
import { FEE, FEES, invalidateLoans, type LoanDetail, type LoanFee } from '../api';
import { presentRefusal } from '../refusal';
import { FEE_PATHS, feeSchema, feeValuesOf, NEW_FEE_KINDS, toFeeBody, type FeeValues } from './fee';

const input = 'rounded-md border px-3 py-2';

/**
 * 3B2 design §6: adding fees (`editing` absent: POST) or correcting them inline on their row (PUT, with the version
 * read). The section announces and places the focus: this form is gone by then.
 */
export function FeeForm({ loan, editing, onDone }: { loan: LoanDetail; editing?: LoanFee; onDone: (message?: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const headingId = useId();
  // The version the form opened on: a refetch meanwhile must not turn the expected 409 into an overwrite.
  const [version] = useState(editing?.version);
  const form = useForm<FeeValues>({ resolver: zodResolver(feeSchema(t)), defaultValues: feeValuesOf(editing) });
  const errors = useSubmitErrors(form.setError, FEE_PATHS);
  // Mount only: opening the form lands the person in it (the kind when adding, the amount when correcting).
  useEffect(() => { form.setFocus(editing ? 'amount' : 'kind'); }, []); // eslint-disable-line react-hooks/exhaustive-deps
  const add = $api.useMutation('post', FEES);
  const correct = $api.useMutation('put', FEE);
  // An unlinked penalty keeps its kind when corrected; a new fee is never one.
  const kinds = editing?.kind === 'early_repayment_penalty' ? [...NEW_FEE_KINDS, 'early_repayment_penalty' as const] : NEW_FEE_KINDS;

  const submit = form.handleSubmit((values) => {
    errors.clear();
    const callbacks = {
      onSuccess: () => {
        void invalidateLoans(queryClient);
        onDone(editing ? t('loan_fees.corrected') : t('loan_fees.added'));
      },
      onError: (error: unknown) => errors.onError(error),
    };
    if (editing) correct.mutate({ params: { path: { id: loan.id, feeId: editing.id } }, body: toFeeBody(values, version) }, callbacks);
    else add.mutate({ params: { path: { id: loan.id } }, body: toFeeBody(values) }, callbacks);
  });

  const e = form.formState.errors;
  const problem = errors.problem ? presentRefusal(t, editing ? correct.error : add.error) : null;

  return (
    <form aria-labelledby={headingId} noValidate onSubmit={(ev) => void submit(ev)} className="max-w-md space-y-3">
      <h3 id={headingId} className="font-semibold">{editing ? t('loan_fees.edit_title') : t('loan_fees.add_title')}</h3>
      {problem ? (
        <>
          <ProblemMessage problem={problem} />
          {/* The row changed or went: reading it again closes the form onto what is now stored. */}
          {problem.kind === 'conflict' || problem.kind === 'not_found' ? <Reload onReload={() => invalidateLoans(queryClient).then(() => onDone())} /> : null}
        </>
      ) : null}
      <Field label={t('loan_fees.kind')} error={e.kind?.message}>
        {(c) => (
          <select {...c} {...form.register('kind')} className={input}>
            {kinds.map((k) => <option key={k} value={k}>{t(`loan_detail.fee_kind.${k}`)}</option>)}
          </select>
        )}
      </Field>
      <Field label={t('loan_fees.amount')} error={e.amount?.message}>
        {(c) => <input inputMode="decimal" {...c} {...form.register('amount')} className={`w-32 ${input}`} />}
      </Field>
      <Field label={t('loan_fees.date')} error={e.onDate?.message}>
        {(c) => <input type="date" {...c} {...form.register('onDate')} className={input} />}
      </Field>
      <Field label={t('loan_fees.note')} error={e.note?.message}>
        {(c) => <textarea {...c} {...form.register('note')} className={`w-full ${input}`} />}
      </Field>
      <div className="flex items-center gap-2">
        <Button type="submit" disabled={add.isPending || correct.isPending}>{editing ? t('loan_detail.save') : t('loan_fees.submit')}</Button>
        <Button type="button" variant="ghost" onClick={() => onDone()}>{t('loan_detail.cancel')}</Button>
      </div>
    </form>
  );
}

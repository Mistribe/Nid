import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { useRef, useState } from 'react';
import { Controller, useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { DateField } from '@/components/form/DateField';
import { SelectField } from '@/components/form/SelectField';
import { ModalDialog } from '@/components/ModalDialog';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Textarea } from '@/components/ui/textarea';
import { Field } from '@/components/form/Field';
import { Reload } from '@/components/form/Reload';
import { useSubmitErrors } from '@/components/form/useSubmitErrors';
import { FEE, FEES, invalidateLoans, type LoanDetail, type LoanFee } from '@/api/loans';
import { presentRefusal } from '../refusal';
import { FEE_PATHS, feeSchema, feeValuesOf, NEW_FEE_KINDS, toFeeBody, type FeeValues } from './fee';
import '@/locales/loans';

/**
 * The fee dialog, open while mounted: adding fees (`editing` absent: POST) or correcting one (PUT, with the version
 * read). A save hands the dialog back (`close()`) and the page-side section announces and places the focus; « Annuler » and
 * Escape just call `onClose`.
 */
export function FeeDialog({ loan, editing, onClose, onDone }: {
  loan: LoanDetail; editing?: LoanFee; onClose: () => void; onDone: (message: string) => void;
}) {
  const { t } = useTranslation();
  const dialog = useRef<{ close: () => void }>(null);
  return (
    <ModalDialog title={editing ? t('loan_fees.edit_title') : t('loan_fees.add_title')} onClose={onClose} dialogRef={dialog}>
      {(titleId) => (
        <FeeForm loan={loan} editing={editing} titleId={titleId}
          onDone={(message) => { if (message) { dialog.current?.close(); onDone(message); } else onClose(); }} />
      )}
    </ModalDialog>
  );
}

function FeeForm({ loan, editing, titleId, onDone }: { loan: LoanDetail; editing?: LoanFee; titleId: string; onDone: (message?: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  // The version the form opened on: a refetch meanwhile must not turn the expected 409 into an overwrite.
  const [version] = useState(editing?.version);
  const form = useForm<FeeValues>({ resolver: zodResolver(feeSchema(t)), defaultValues: feeValuesOf(editing) });
  const errors = useSubmitErrors(form.setError, FEE_PATHS);
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
    <form aria-labelledby={titleId} noValidate onSubmit={(ev) => { ev.stopPropagation(); void submit(ev); }} className="space-y-3">
      {problem ? (
        <>
          <ProblemMessage problem={problem} />
          {/* The row changed or went: reading it again closes the dialog onto what is now stored. */}
          {problem.kind === 'conflict' || problem.kind === 'not_found' ? <Reload onReload={() => invalidateLoans(queryClient).then(() => onDone())} /> : null}
        </>
      ) : null}
      <div className="grid gap-3 sm:grid-cols-2">
        <Controller control={form.control} name="kind" render={({ field }) => (
          <Field label={t('loan_fees.kind')} error={e.kind?.message}>
            {(c) => <SelectField {...c} value={field.value} onValueChange={field.onChange}
              options={kinds.map((k) => ({ value: k, label: t(`loan_detail.fee_kind.${k}`) }))} />}
          </Field>
        )} />
        <Field label={t('loan_fees.amount')} error={e.amount?.message}>
          {(c) => <Input inputMode="decimal" {...c} {...form.register('amount')} />}
        </Field>
        <Controller control={form.control} name="onDate" render={({ field }) => (
          <Field label={t('loan_fees.date')} error={e.onDate?.message}>
            {(c) => <DateField {...c} value={field.value} onChange={field.onChange} />}
          </Field>
        )} />
      </div>
      <Field label={t('loan_fees.note')} error={e.note?.message}>
        {(c) => <Textarea {...c} {...form.register('note')} />}
      </Field>
      <div className="flex justify-end gap-2">
        <Button type="button" variant="outline" onClick={() => onDone()}>{t('loan_detail.cancel')}</Button>
        <Button type="submit" disabled={add.isPending || correct.isPending}>{editing ? t('loan_detail.save') : t('loan_fees.submit')}</Button>
      </div>
    </form>
  );
}

import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { useId, useRef, useState } from 'react';
import { Controller, useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { DateField } from '@/components/form/DateField';
import { Segmented } from '@/components/form/Segmented';
import { ModalDialog } from '@/components/ModalDialog';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Textarea } from '@/components/ui/textarea';
import { Field } from '@/components/form/Field';
import { Reload } from '../../subscriptions/edits/Reload';
import { useSubmitErrors } from '../../subscriptions/edits/useSubmitErrors';
import { invalidateLoans, REPAYMENT, REPAYMENTS, type LoanDetail, type LoanRepayment } from '../api';
import { EFFECTS } from '../format';
import { presentRefusal } from '../refusal';
import { REPAYMENT_PATHS, repaymentSchema, repaymentValuesOf, toRepaymentBody, type RepaymentPrefill, type RepaymentValues } from './repayment';

/**
 * The repayment dialog, open while mounted: adding an early repayment (`editing` absent: POST, possibly prefilled from a
 * simulation) or correcting one (PUT, with the version read). A save hands the dialog back and the section announces
 * and places the focus; « Annuler » and Escape just call `onClose`.
 */
export function RepaymentDialog({ loan, editing, prefill, onClose, onDone }: {
  loan: LoanDetail; editing?: LoanRepayment; prefill?: RepaymentPrefill | null; onClose: () => void; onDone: (message: string) => void;
}) {
  const { t } = useTranslation();
  const dialog = useRef<{ close: () => void }>(null);
  return (
    <ModalDialog title={editing ? t('loan_repayments.edit_title') : t('loan_repayments.add_title')} onClose={onClose} dialogRef={dialog}>
      {(titleId) => (
        <RepaymentForm loan={loan} editing={editing} prefill={prefill} titleId={titleId}
          onDone={(message) => { if (message) { dialog.current?.close(); onDone(message); } else onClose(); }} />
      )}
    </ModalDialog>
  );
}

function RepaymentForm({ loan, editing, prefill, titleId, onDone }: {
  loan: LoanDetail; editing?: LoanRepayment; prefill?: RepaymentPrefill | null; titleId: string; onDone: (message?: string) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  // The version the form opened on: a refetch meanwhile must not turn the expected 409 into an overwrite.
  const [version] = useState(editing?.version);
  const effectLabelId = useId();
  const effectErrorId = useId();
  const form = useForm<RepaymentValues>({ resolver: zodResolver(repaymentSchema(t)), defaultValues: repaymentValuesOf(editing, prefill) });
  const errors = useSubmitErrors(form.setError, REPAYMENT_PATHS);
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
    <form aria-labelledby={titleId} noValidate onSubmit={(ev) => { ev.stopPropagation(); void submit(ev); }} className="space-y-3">
      {problem ? (
        <>
          <ProblemMessage problem={problem} />
          {/* The row changed or went: reading it again closes the dialog onto what is now stored. */}
          {problem.kind === 'conflict' || problem.kind === 'not_found' ? <Reload onReload={() => invalidateLoans(queryClient).then(() => onDone())} /> : null}
        </>
      ) : null}
      <div className="grid gap-3 sm:grid-cols-2">
        <Controller control={form.control} name="onDate" render={({ field }) => (
          <Field label={t('loan_repayments.date')} error={e.onDate?.message}>
            {(c) => <DateField {...c} value={field.value} onChange={field.onChange} />}
          </Field>
        )} />
        <Field label={t('loan_repayments.amount')} error={e.amount?.message}>
          {(c) => <Input inputMode="decimal" {...c} {...form.register('amount')} />}
        </Field>
      </div>
      <div className="space-y-1">
        <span id={effectLabelId} className="block text-sm">{t('loan_repayments.effect')}</span>
        {/* No default: neither effect is checked until the person picks one, and nothing is sent before. */}
        <Controller control={form.control} name="effect" render={({ field }) => (
          <Segmented value={field.value ?? ''} onValueChange={field.onChange} aria-labelledby={effectLabelId}
            aria-describedby={e.effect ? effectErrorId : undefined}
            options={EFFECTS.map((v) => ({ value: v, label: t(`loan_repayments.effect_option.${v}`) }))} />
        )} />
        {e.effect ? <span id={effectErrorId} className="block text-sm text-destructive">{e.effect.message}</span> : null}
      </div>
      <Field label={t('loan_repayments.penalty')} hint={t('loan_repayments.penalty_help')} error={e.penalty?.message}>
        {(c) => <Input inputMode="decimal" {...c} {...form.register('penalty')} />}
      </Field>
      <Field label={t('loan_repayments.note')} error={e.note?.message}>
        {(c) => <Textarea {...c} {...form.register('note')} />}
      </Field>
      <div className="flex justify-end gap-2">
        <Button type="button" variant="outline" onClick={() => onDone()}>{t('loan_detail.cancel')}</Button>
        <Button type="submit" disabled={add.isPending || correct.isPending}>{editing ? t('loan_detail.save') : t('loan_repayments.submit')}</Button>
      </div>
    </form>
  );
}

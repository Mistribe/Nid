import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useId } from 'react';
import { useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { placeFieldError, presentConflict } from '../../investments/entries/entries';
import { Field } from '../../subscriptions/edits/Field';
import { Reload } from '../../subscriptions/edits/Reload';
import { useSubmitErrors } from '../../subscriptions/edits/useSubmitErrors';
import { todayIso } from '../../subscriptions/today';
import { invalidateIncome, ONE_OFF, ONE_OFFS, type IncomeOneOff, type IncomeSourceDetail } from '../api';
import { ONE_OFF_FALLBACK, ONE_OFF_PATHS, oneOffSchema, oneOffValuesOf, toWireOneOff, type OneOffValues } from './oneOff';

const input = 'rounded-md border px-3 py-2';

/**
 * Spec §4: adding a one-off (`editing` absent: POST) or correcting one inline on its row (PUT). A corrected one-off is a
 * Core flow and gets a new id, so the confirmation is announced and focused by the section, not by this soon-gone form.
 */
export function OneOffForm({ source, editing, onDone }: {
  source: IncomeSourceDetail; editing?: IncomeOneOff; onDone: (message?: string) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const headingId = useId();
  const form = useForm<OneOffValues>({ resolver: zodResolver(oneOffSchema(t, todayIso())), defaultValues: oneOffValuesOf(editing) });
  const errors = useSubmitErrors(form.setError, ONE_OFF_PATHS);
  // Mount only: opening the form lands the person in it (the date when adding, the amount when correcting).
  useEffect(() => { form.setFocus(editing ? 'amount' : 'occurredOn'); }, []); // eslint-disable-line react-hooks/exhaustive-deps
  const add = $api.useMutation('post', ONE_OFFS);
  const correct = $api.useMutation('put', ONE_OFF);

  const submit = form.handleSubmit((values) => {
    errors.clear();
    const body = toWireOneOff(values, source.currency);
    const callbacks = {
      onSuccess: () => {
        void invalidateIncome(queryClient);
        onDone(editing ? t('income_detail.one_offs.corrected') : t('income_detail.one_offs.added'));
      },
      onError: (error: unknown) => placeFieldError(error, t, form.setError, errors, ONE_OFF_FALLBACK),
    };
    if (editing) correct.mutate({ params: { path: { id: source.id, flowId: editing.id } }, body }, callbacks);
    else add.mutate({ params: { path: { id: source.id } }, body }, callbacks);
  });

  const e = form.formState.errors;
  const problem = errors.problem?.kind === 'conflict' ? presentConflict(t, editing ? correct.error : add.error) : errors.problem;

  return (
    <form aria-labelledby={headingId} noValidate onSubmit={(ev) => void submit(ev)} className="max-w-md space-y-3">
      <h3 id={headingId} className="font-semibold">{editing ? t('income_detail.one_offs.edit_title') : t('income_detail.one_offs.add_title')}</h3>
      {problem ? (
        <>
          <ProblemMessage problem={problem} />
          {problem.kind === 'conflict' || problem.kind === 'not_found' ? <Reload onReload={() => invalidateIncome(queryClient)} /> : null}
        </>
      ) : null}
      <Field label={t('income_detail.one_offs.date')} hint={t('income_detail.one_offs.date_help')} error={e.occurredOn?.message}>
        {(c) => <input type="date" max={todayIso()} {...c} {...form.register('occurredOn')} className={input} />}
      </Field>
      <Field label={t('income_detail.one_offs.amount')} error={e.amount?.message}>
        {(c) => <input inputMode="decimal" {...c} {...form.register('amount')} className={`w-32 ${input}`} />}
      </Field>
      <Field label={t('income_detail.one_offs.note')} error={e.note?.message}>
        {(c) => <textarea {...c} {...form.register('note')} className={`w-full ${input}`} />}
      </Field>
      <div className="flex items-center gap-2">
        <Button type="submit" disabled={add.isPending || correct.isPending}>{editing ? t('income_detail.save') : t('income_detail.one_offs.submit')}</Button>
        <Button type="button" variant="ghost" onClick={() => onDone()}>{t('income_detail.cancel')}</Button>
      </div>
    </form>
  );
}

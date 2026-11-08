import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { Controller, useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { DateField } from '@/components/form/DateField';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Textarea } from '@/components/ui/textarea';
import { placeFieldError, presentConflict } from '@/components/form/fieldErrors';
import { Field } from '@/components/form/Field';
import { Reload } from '@/components/form/Reload';
import { useSubmitErrors } from '@/components/form/useSubmitErrors';
import { todayIso } from '@/lib/today';
import { invalidateIncome, ONE_OFF, ONE_OFFS, type IncomeOneOff, type IncomeSourceDetail } from '../api';
import { ONE_OFF_FALLBACK, ONE_OFF_PATHS, oneOffSchema, oneOffValuesOf, toWireOneOff, type OneOffValues } from './oneOff';
import '@/locales/income';

/**
 * Spec §4: adding a one-off (`editing` absent: POST) or correcting one (PUT), in a dialog. A corrected one-off is a Core
 * flow and gets a new id, so the confirmation is announced and focused by the section, not by this soon-gone form.
 */
export function OneOffForm({ source, editing, titleId, onDone }: {
  source: IncomeSourceDetail; editing?: IncomeOneOff; titleId: string; onDone: (message?: string) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  // An ended source takes one-offs up to its end only.
  const last = source.endedOn ?? todayIso();
  const form = useForm<OneOffValues>({ resolver: zodResolver(oneOffSchema(t, last, source.endedOn != null)), defaultValues: oneOffValuesOf(editing, last) });
  const errors = useSubmitErrors(form.setError, ONE_OFF_PATHS);
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
    <form aria-labelledby={titleId} noValidate onSubmit={(ev) => { ev.stopPropagation(); void submit(ev); }} className="space-y-3">
      {problem ? (
        <>
          <ProblemMessage problem={problem} />
          {problem.kind === 'conflict' || problem.kind === 'not_found' ? <Reload onReload={() => invalidateIncome(queryClient)} /> : null}
        </>
      ) : null}
      <div className="grid gap-3 sm:grid-cols-2">
        <Controller control={form.control} name="occurredOn" render={({ field }) => (
          <Field label={t('income_detail.one_offs.date')} hint={t('income_detail.one_offs.date_help')} error={e.occurredOn?.message}>
            {(c) => <DateField {...c} value={field.value} onChange={field.onChange} max={last} />}
          </Field>
        )} />
        <Field label={t('income_detail.one_offs.amount')} hint={t('income_form.net_hint')} error={e.amount?.message}>
          {(c) => <Input inputMode="decimal" {...c} {...form.register('amount')} />}
        </Field>
      </div>
      {source.kind === 'salary' ? (
        <Field label={t('income_form.taxable_amount')} hint={t('income_form.taxable_hint')} error={e.taxableAmount?.message}>
          {(c) => <Input inputMode="decimal" {...c} {...form.register('taxableAmount')} />}
        </Field>
      ) : null}
      <Field label={t('income_detail.one_offs.note')} error={e.note?.message}>
        {(c) => <Textarea {...c} {...form.register('note')} />}
      </Field>
      <div className="flex justify-end gap-2">
        <Button type="button" variant="outline" onClick={() => onDone()}>{t('income_detail.cancel')}</Button>
        <Button type="submit" disabled={add.isPending || correct.isPending}>{editing ? t('income_detail.save') : t('income_detail.one_offs.submit')}</Button>
      </div>
    </form>
  );
}

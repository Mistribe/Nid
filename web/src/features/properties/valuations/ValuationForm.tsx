import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import type { TFunction } from 'i18next';
import { useEffect, useId } from 'react';
import { useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';
import { useApi } from '@/api/ApiProvider';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { LOAN_AMOUNT } from '../../loans/new/newLoan';
import { presentRefusal } from '../../loans/refusal';
import { ISO_DATE, toWireAmount } from '../../subscriptions/edits/amount';
import { Field } from '../../subscriptions/edits/Field';
import { useSubmitErrors } from '../../subscriptions/edits/useSubmitErrors';
import { todayIso } from '../../subscriptions/today';
import { invalidateProperties, VALUATIONS, type PropertyDetail } from '../api';

const input = 'rounded-md border px-3 py-2';
const PATHS = { asOf: 'asOf', amount: 'amount', note: 'note' } as const;

// The server's bounds: dated on or before today, positive, two decimals (spec 3C1 §3).
const schema = (t: TFunction) => z.object({
  asOf: z.string().regex(ISO_DATE, t('edit.required')).refine((v) => v <= todayIso(), t('field_error.core_date_in_future')),
  amount: z.string().trim().regex(LOAN_AMOUNT, t('loan_form.amount_format')),
  note: z.string().max(2000, t('investments_form.note_too_long')),
});

type ValuationValues = z.infer<ReturnType<typeof schema>>;

/**
 * 3D1 design §6: an estimate added inline — a date, a value, a note. On a date already estimated it replaces that one,
 * and the form says so before sending. The section announces and places the focus: this form is gone by then.
 */
export function ValuationForm({ property, onDone }: { property: PropertyDetail; onDone: (message?: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const headingId = useId();
  const form = useForm<ValuationValues>({ resolver: zodResolver(schema(t)), defaultValues: { asOf: todayIso(), amount: '', note: '' } });
  const errors = useSubmitErrors(form.setError, PATHS);
  // Mount only: opening the form lands the person on its date.
  useEffect(() => { form.setFocus('asOf'); }, []); // eslint-disable-line react-hooks/exhaustive-deps
  const add = $api.useMutation('post', VALUATIONS);
  const replaces = property.valuations.some((v) => v.asOf === form.watch('asOf'));

  const submit = form.handleSubmit((values) => {
    errors.clear();
    add.mutate(
      { params: { path: { id: property.id } }, body: { asOf: values.asOf, amount: toWireAmount(values.amount), ...(values.note.trim() ? { note: values.note } : {}) } },
      {
        onSuccess: () => {
          void invalidateProperties(queryClient);
          onDone(replaces ? t('property_valuations.replaced') : t('property_valuations.added'));
        },
        onError: (error) => errors.onError(error),
      },
    );
  });

  const e = form.formState.errors;
  const problem = errors.problem ? presentRefusal(t, add.error) : null;

  return (
    <form aria-labelledby={headingId} noValidate onSubmit={(ev) => void submit(ev)} className="max-w-md space-y-3">
      <h3 id={headingId} className="font-semibold">{t('property_valuations.add_title')}</h3>
      {problem ? <ProblemMessage problem={problem} /> : null}
      <Field label={t('property_valuations.date')} hint={replaces ? t('property_valuations.replaces') : undefined} error={e.asOf?.message}>
        {(c) => <input type="date" max={todayIso()} {...c} {...form.register('asOf')} className={input} />}
      </Field>
      <Field label={t('property_valuations.value')} error={e.amount?.message}>
        {(c) => <input inputMode="decimal" {...c} {...form.register('amount')} className={`w-40 ${input}`} />}
      </Field>
      <Field label={t('property_valuations.note')} error={e.note?.message}>
        {(c) => <textarea {...c} {...form.register('note')} className={`w-full ${input}`} />}
      </Field>
      <div className="flex items-center gap-2">
        <Button type="submit" disabled={add.isPending}>{t('property_valuations.submit')}</Button>
        <Button type="button" variant="ghost" onClick={() => onDone()}>{t('loan_detail.cancel')}</Button>
      </div>
    </form>
  );
}

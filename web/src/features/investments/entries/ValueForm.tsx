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
import { toFieldAmount, toWireAmount, trimAmount } from '@/lib/amount';
import { Field } from '@/components/form/Field';
import { Reload } from '@/components/form/Reload';
import { useSubmitErrors } from '@/components/form/useSubmitErrors';
import { todayIso } from '@/lib/today';
import { invalidateInvestments, VALUATIONS, type EnvelopeDetail, type Valuation } from '@/api/investments';
import { placeFieldError, presentConflict } from '@/components/form/fieldErrors';
import { valueSchema } from './entries';
import '@/locales/investments';

const PATHS = { asOf: 'asOf', amount: 'amount', note: 'note' } as const;
const FALLBACK: Record<string, 'asOf' | 'amount'> = {
  'core.date_in_future': 'asOf', 'core.currency_mismatch': 'amount', 'core.invalid_fact': 'amount',
};

/**
 * The body of the « Nouvelle valeur » / « Corriger la valeur » dialog (2B2a design §6): both post the same
 * `VALUATIONS` — the server treats a value on an already-known date as its replacement, so once correcting one the
 * date is read-only. `onDone` is called with the confirmation message on save (the page announces it and owns the
 * focus: a correction gets a new id), or with no message when « Annuler » just abandons the form.
 */
export function ValueForm({ envelope, editing, titleId, onDone }: {
  envelope: EnvelopeDetail; editing?: Valuation; titleId: string; onDone: (message?: string) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const last = [...envelope.valuations].sort((a, b) => (a.asOf < b.asOf ? 1 : a.asOf > b.asOf ? -1 : 0))[0];
  const form = useForm({
    resolver: zodResolver(valueSchema(t)),
    defaultValues: {
      asOf: editing?.asOf ?? todayIso(),
      amount: editing ? toFieldAmount(trimAmount(editing.amount.amount)) : last ? toFieldAmount(trimAmount(last.amount.amount)) : '',
      note: editing?.note ?? '',
    },
  });
  const errors = useSubmitErrors(form.setError, PATHS);
  const save = $api.useMutation('post', VALUATIONS, {
    onSuccess: () => { errors.clear(); void invalidateInvestments(queryClient); onDone(t('envelope.value_saved')); },
    onError: (error) => placeFieldError(error, t, form.setError, errors, FALLBACK),
  });

  const submit = form.handleSubmit((values) => {
    save.mutate({
      params: { path: { id: envelope.id } },
      body: { asOf: values.asOf, amount: { amount: toWireAmount(values.amount), currency: envelope.currency }, note: values.note || undefined },
    });
  });

  const e = form.formState.errors;
  const asOf = form.watch('asOf');
  const replaces = !editing && envelope.valuations.some((v) => v.asOf === asOf);
  // A conflict shows its own sentence when known (final findings #5), read from the mutation's own error rather
  // than `errors.problem` (which only carries the generic text `present()` gives every 409).
  const problem = errors.problem?.kind === 'conflict' ? presentConflict(t, save.error) : errors.problem;

  return (
    <form aria-labelledby={titleId} onSubmit={(ev) => { ev.stopPropagation(); void submit(ev); }} className="space-y-3" noValidate>
      {problem ? (
        <>
          <ProblemMessage problem={problem} />
          {problem.kind === 'conflict' ? <Reload onReload={() => invalidateInvestments(queryClient)} /> : null}
        </>
      ) : null}
      <div className="grid gap-3 sm:grid-cols-2">
        <Controller control={form.control} name="asOf" render={({ field }) => (
          <Field label={t('envelope.date')} hint={replaces ? t('envelope.replaces') : undefined} error={e.asOf?.message}>
            {(c) => <DateField {...c} max={todayIso()} value={field.value} onChange={field.onChange} readOnly={!!editing} />}
          </Field>
        )} />
        <Field label={t('envelope.amount')} error={e.amount?.message}>
          {(c) => <Input inputMode="decimal" {...c} {...form.register('amount')} />}
        </Field>
      </div>
      <Field label={t('envelope.note')} error={e.note?.message}>
        {(c) => <Textarea {...c} {...form.register('note')} />}
      </Field>
      <div className="flex justify-end gap-2">
        <Button type="button" variant="outline" onClick={() => onDone()}>{t('envelope.cancel')}</Button>
        <Button type="submit" disabled={save.isPending}>{editing ? t('envelope.save') : t('envelope.save_value')}</Button>
      </div>
    </form>
  );
}

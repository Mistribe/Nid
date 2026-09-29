import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { useId, useRef, useState } from 'react';
import { useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { toWireAmount } from '../../subscriptions/edits/amount';
import { Field } from '../../subscriptions/edits/Field';
import { Reload } from '../../subscriptions/edits/Reload';
import { useSubmitErrors } from '../../subscriptions/edits/useSubmitErrors';
import { todayIso } from '../../subscriptions/today';
import { invalidateInvestments, VALUATIONS, type EnvelopeDetail, type Valuation } from '../api';
import { placeFieldError, presentConflict, trimAmount, valueSchema } from './entries';

const input = 'rounded-md border px-3 py-2';
const PATHS = { asOf: 'asOf', amount: 'amount', note: 'note' } as const;
const FALLBACK: Record<string, 'asOf' | 'amount'> = {
  'core.date_in_future': 'asOf', 'core.currency_mismatch': 'amount', 'core.invalid_fact': 'amount',
};

/**
 * The quick value at the top of the envelope's page (`editing` absent), or a correction inline on its row (2B2a
 * design §6): both post the same `VALUATIONS` — the server treats a value on an already-known date as its
 * replacement, so once correcting one the date is read-only. The row form closes on save: `onDone` is called with
 * its confirmation message (announced and focused by the page, since a correction gets a new id and this very
 * component is about to unmount), or with no message when « Annuler » just abandons the edit.
 */
export function ValueForm({ envelope, editing, onDone }: { envelope: EnvelopeDetail; editing?: Valuation; onDone?: (message?: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const [saved, setSaved] = useState(false);
  const headingId = useId();
  const submittedAmount = useRef('');
  const last = [...envelope.valuations].sort((a, b) => (a.asOf < b.asOf ? 1 : a.asOf > b.asOf ? -1 : 0))[0];
  const form = useForm({
    resolver: zodResolver(valueSchema(t)),
    defaultValues: {
      asOf: editing?.asOf ?? todayIso(),
      amount: editing ? trimAmount(editing.amount.amount) : last ? trimAmount(last.amount.amount) : '',
      note: editing?.note ?? '',
    },
  });
  const errors = useSubmitErrors(form.setError, PATHS);
  const save = $api.useMutation('post', VALUATIONS, {
    onSuccess: () => {
      errors.clear();
      void invalidateInvestments(queryClient);
      if (editing) { onDone?.(t('envelope.value_saved')); return; }
      // The last value shown next time is the one just saved (spec §6), trimmed the same way as any other default.
      setSaved(true);
      form.reset({ asOf: todayIso(), amount: trimAmount(toWireAmount(submittedAmount.current)), note: '' });
    },
    onError: (error) => { setSaved(false); placeFieldError(error, t, form.setError, errors, FALLBACK); },
  });

  const submit = form.handleSubmit((values) => {
    setSaved(false);
    submittedAmount.current = values.amount;
    save.mutate({
      params: { path: { id: envelope.id } },
      body: { asOf: values.asOf, amount: { amount: toWireAmount(values.amount), currency: envelope.currency }, note: values.note || undefined },
    });
  });

  const e = form.formState.errors;
  const asOf = form.watch('asOf');
  const replaces = !editing && envelope.valuations.some((v) => v.asOf === asOf);
  const heading = editing ? t('envelope.edit_value') : t('envelope.new_value');
  // A conflict shows its own sentence when known (final findings #5), read from the mutation's own error rather
  // than `errors.problem` (which only carries the generic text `present()` gives every 409).
  const problem = errors.problem?.kind === 'conflict' ? presentConflict(t, save.error) : errors.problem;

  return (
    <form aria-labelledby={headingId} onSubmit={(ev) => void submit(ev)} className="max-w-md space-y-3" noValidate>
      <h3 id={headingId} className="font-semibold">{heading}</h3>
      {problem ? (
        <>
          <ProblemMessage problem={problem} />
          {problem.kind === 'conflict' ? <Reload onReload={() => invalidateInvestments(queryClient)} /> : null}
        </>
      ) : null}
      {/* Mounted empty so a screen reader is already watching it when the text arrives (it is the change, not the
       * element's appearance, that must be announced). */}
      <p role="status" aria-live="polite">{saved ? t('envelope.value_saved') : ''}</p>
      <Field label={t('envelope.date')} hint={replaces ? t('envelope.replaces') : undefined} error={e.asOf?.message}>
        {(c) => <input type="date" max={todayIso()} readOnly={!!editing} {...c} {...form.register('asOf')} className={input} />}
      </Field>
      <Field label={t('envelope.amount')} error={e.amount?.message}>
        {(c) => <input inputMode="decimal" {...c} {...form.register('amount')} className={`w-32 ${input}`} />}
      </Field>
      <Field label={t('envelope.note')} error={e.note?.message}>
        {(c) => <textarea {...c} {...form.register('note')} className={`w-full ${input}`} />}
      </Field>
      <div className="flex items-center gap-2">
        <Button type="submit" disabled={save.isPending}>{editing ? t('envelope.save') : t('envelope.save_value')}</Button>
        {editing ? <Button type="button" variant="ghost" onClick={() => onDone?.()}>{t('envelope.cancel')}</Button> : null}
      </div>
    </form>
  );
}

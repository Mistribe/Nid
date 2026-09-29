import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { useId, useState } from 'react';
import { useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { toProblem } from '@/api/errors';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { toWireAmount } from '../../subscriptions/edits/amount';
import { Field } from '../../subscriptions/edits/Field';
import { Reload } from '../../subscriptions/edits/Reload';
import { useSubmitErrors } from '../../subscriptions/edits/useSubmitErrors';
import { fieldErrorsOf, messageFor } from '../../subscriptions/fieldErrors';
import { todayIso } from '../../subscriptions/today';
import { invalidateInvestments, VALUATIONS, type EnvelopeDetail, type Valuation } from '../api';
import { trimAmount, valueSchema } from './entries';

const input = 'rounded-md border px-3 py-2';
const PATHS = { asOf: 'asOf', amount: 'amount', note: 'note' } as const;

/** A pathless Core refusal (Task 3: these codes carry no `errors[]`) attributed to the one field it can concern. */
function fallbackField(code: string | null | undefined): 'asOf' | 'amount' | undefined {
  if (code === 'core.date_in_future') return 'asOf';
  if (code === 'core.currency_mismatch' || code === 'core.invalid_fact') return 'amount';
  return undefined;
}

/**
 * The quick value at the top of the envelope's page (`editing` absent), or a correction inline on its row (2B2a
 * design §6): both post the same `VALUATIONS` — the server treats a value on an already-known date as its
 * replacement, so once correcting one the date is read-only.
 */
export function ValueForm({ envelope, editing, onDone }: { envelope: EnvelopeDetail; editing?: Valuation; onDone?: () => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const [saved, setSaved] = useState(false);
  const headingId = useId();
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
      setSaved(true);
      errors.clear();
      void invalidateInvestments(queryClient);
      // A correction stays open on its « Enregistré » status; the person dismisses it with « Annuler » (onDone),
      // rather than the row silently snapping shut the instant the status would have appeared.
      if (!editing) form.reset({ asOf: todayIso(), amount: '', note: '' });
    },
    onError: (error) => {
      setSaved(false);
      const known = fieldErrorsOf(error);
      const field = known.length === 0 ? fallbackField(toProblem(error).code) : undefined;
      if (field) {
        const problem = toProblem(error);
        form.setError(field, { type: 'server', message: messageFor(t, { path: field, code: problem.code ?? '', message: problem.title ?? '' }) });
        errors.clear();
      } else {
        errors.onError(error);
      }
    },
  });

  const submit = form.handleSubmit((values) => {
    setSaved(false);
    save.mutate({
      params: { path: { id: envelope.id } },
      body: { asOf: values.asOf, amount: { amount: toWireAmount(values.amount), currency: envelope.currency }, note: values.note || undefined },
    });
  });

  const e = form.formState.errors;
  const asOf = form.watch('asOf');
  const replaces = !editing && envelope.valuations.some((v) => v.asOf === asOf);
  const heading = editing ? t('envelope.edit_value') : t('envelope.new_value');

  return (
    <form aria-labelledby={headingId} onSubmit={(ev) => void submit(ev)} className="max-w-md space-y-3" noValidate>
      <h3 id={headingId} className="font-semibold">{heading}</h3>
      {errors.problem ? <><ProblemMessage problem={errors.problem} />{errors.problem.kind === 'conflict' ? <Reload /> : null}</> : null}
      {saved ? <p role="status">{t('envelope.value_saved')}</p> : null}
      <Field label={t('envelope.date')} error={e.asOf?.message}>
        {(c) => <input type="date" readOnly={!!editing} {...c} {...form.register('asOf')} className={input} />}
      </Field>
      {replaces ? <p>{t('envelope.replaces')}</p> : null}
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

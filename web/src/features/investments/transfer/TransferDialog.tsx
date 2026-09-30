import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useId, useRef } from 'react';
import { createPortal } from 'react-dom';
import { useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { ISO_DATE, toWireAmount } from '../../subscriptions/edits/amount';
import { Field } from '../../subscriptions/edits/Field';
import { Reload } from '../../subscriptions/edits/Reload';
import { useSubmitErrors } from '../../subscriptions/edits/useSubmitErrors';
import { todayIso } from '../../subscriptions/today';
import { ENVELOPES, invalidateInvestments, TRANSFERS, type EnvelopeDetail, type EnvelopeSummary } from '../api';
import { placeFieldError, POSITIVE_AMOUNT, presentConflict } from '../entries/entries';

const input = 'rounded-md border px-3 py-2';
const PATHS = { to: 'to', occurredOn: 'occurredOn', amount: 'amount', note: 'note' } as const;
const FALLBACK: Record<string, 'occurredOn' | 'amount'> = {
  'core.date_in_future': 'occurredOn', 'core.amount_not_positive': 'amount', 'core.currency_mismatch': 'amount',
};

/**
 * 2B2b design: a transfer from `source` to another open envelope of the same currency that the person can write
 * (the server checks all of it again: a destination it hides answers 404). A native modal dialog in a portal, on
 * the `NewProviderDialog` pattern; the page owns its open state, the focus going back to its button, and the
 * announcement of the success (`onDone`), so a dialog closed by Escape or « Annuler » announces nothing.
 */
export function TransferDialog({ source, onClose, onDone }: {
  source: EnvelopeDetail; onClose: () => void; onDone: (message: string) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const ref = useRef<HTMLDialogElement>(null);
  const titleId = useId();
  const envelopes = $api.useQuery('get', ENVELOPES, { params: { query: { scope: 'me' } } });

  useEffect(() => {
    const dialog = ref.current;
    if (dialog && !dialog.open) dialog.showModal();
    return () => { if (dialog?.open) dialog.close(); };
  }, []);

  const destinations = (envelopes.data?.items ?? []).filter(
    (e) => e.id !== source.id && !e.closed && e.canWrite && e.currency === source.currency);

  return createPortal(
    <dialog ref={ref} aria-labelledby={titleId}
      onCancel={(event) => { event.preventDefault(); onClose(); }}
      onKeyDown={(event) => { if (event.key === 'Escape') { event.preventDefault(); onClose(); } }}
      className="m-auto w-full max-w-md rounded-md border bg-background p-4 text-foreground backdrop:bg-black/50">
      <div className="space-y-3">
        <h2 id={titleId} className="text-lg font-semibold">{t('envelope.transfer.title')}</h2>
        {envelopes.error ? <ProblemMessage problem={present(toProblem(envelopes.error))} /> : null}
        {!envelopes.data && !envelopes.error ? (
          <div aria-busy="true" className="h-20 animate-pulse rounded-md bg-muted"><span className="sr-only">{t('envelope.transfer.loading')}</span></div>
        ) : null}
        {envelopes.data && destinations.length === 0 ? (
          <>
            <p>{t('envelope.transfer.none', { currency: source.currency })}</p>
            <div className="flex justify-end"><Button type="button" variant="outline" onClick={onClose}>{t('envelope.transfer.close')}</Button></div>
          </>
        ) : null}
        {destinations.length > 0 ? <TransferForm source={source} destinations={destinations} onClose={onClose} onDone={onDone} /> : null}
      </div>
    </dialog>,
    document.body,
  );
}

function TransferForm({ source, destinations, onClose, onDone }: {
  source: EnvelopeDetail; destinations: EnvelopeSummary[]; onClose: () => void; onDone: (message: string) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const headingId = useId();
  const schema = z.object({
    to: z.string().min(1, t('edit.required')),
    occurredOn: z.string().regex(ISO_DATE, t('edit.required')),
    amount: z.string().regex(POSITIVE_AMOUNT, t('investments_form.positive_amount_format')),
    note: z.string().max(2000, t('investments_form.note_too_long')),
  });
  const form = useForm({
    resolver: zodResolver(schema),
    defaultValues: { to: destinations[0]?.id ?? '', occurredOn: todayIso(), amount: '', note: '' },
  });
  const errors = useSubmitErrors(form.setError, PATHS);
  // Mutate-level callbacks: react-query drops them once this form unmounted, so a closed dialog never announces.
  const transfer = $api.useMutation('post', TRANSFERS, {
    onSuccess: () => { void invalidateInvestments(queryClient); },
  });

  const submit = form.handleSubmit((values) => {
    errors.clear();
    transfer.mutate(
      {
        body: {
          from: source.id, to: values.to, occurredOn: values.occurredOn,
          amount: { amount: toWireAmount(values.amount), currency: source.currency },
          note: values.note || undefined,
        },
      },
      {
        onSuccess: () => onDone(t('envelope.transfer.saved')),
        onError: (error) => placeFieldError(error, t, form.setError, errors, FALLBACK),
      },
    );
  });

  const e = form.formState.errors;
  const unavailable = transfer.error && toProblem(transfer.error).status === 404;
  const problem = errors.problem?.kind === 'conflict' ? presentConflict(t, transfer.error) : errors.problem;

  return (
    <form aria-labelledby={headingId} noValidate className="space-y-3" onSubmit={(ev) => { ev.stopPropagation(); void submit(ev); }}>
      <p id={headingId} className="sr-only">{t('envelope.transfer.title')}</p>
      {unavailable ? <p role="alert" className="text-sm text-destructive">{t('envelope.transfer.unavailable')}</p> : null}
      {!unavailable && problem ? (
        <>
          <ProblemMessage problem={problem} />
          {problem.kind === 'conflict' ? <Reload onReload={() => invalidateInvestments(queryClient)} /> : null}
        </>
      ) : null}
      <Field label={t('envelope.transfer.to')} error={e.to?.message}>
        {(c) => (
          <select {...c} {...form.register('to')} className={`w-full ${input}`}>
            {destinations.map((d) => <option key={d.id} value={d.id}>{d.displayName}</option>)}
          </select>
        )}
      </Field>
      <Field label={t('envelope.date')} error={e.occurredOn?.message}>
        {(c) => <input type="date" max={todayIso()} {...c} {...form.register('occurredOn')} className={input} />}
      </Field>
      <Field label={t('envelope.amount')} error={e.amount?.message}>
        {(c) => <input inputMode="decimal" {...c} {...form.register('amount')} className={`w-32 ${input}`} />}
      </Field>
      <Field label={t('envelope.note')} error={e.note?.message}>
        {(c) => <textarea {...c} {...form.register('note')} className={`w-full ${input}`} />}
      </Field>
      <div className="flex justify-end gap-2">
        <Button type="button" variant="outline" onClick={onClose}>{t('envelope.cancel')}</Button>
        <Button type="submit" disabled={transfer.isPending}>{t('envelope.transfer.submit')}</Button>
      </div>
    </form>
  );
}

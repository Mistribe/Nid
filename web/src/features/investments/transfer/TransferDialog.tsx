import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect } from 'react';
import { Controller, useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { DateField } from '@/components/form/DateField';
import { SelectField } from '@/components/form/SelectField';
import { ModalDialog } from '@/components/ModalDialog';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Textarea } from '@/components/ui/textarea';
import { toWireAmount } from '../../subscriptions/edits/amount';
import { Field } from '@/components/form/Field';
import { Reload } from '../../subscriptions/edits/Reload';
import { useSubmitErrors } from '../../subscriptions/edits/useSubmitErrors';
import { todayIso } from '../../subscriptions/today';
import { ENVELOPES, invalidateInvestments, TRANSFERS, type EnvelopeDetail, type EnvelopeSummary } from '../api';
import { pastDate, placeFieldError, POSITIVE_AMOUNT, presentConflict } from '../entries/entries';
import '@/locales/investments';

const PATHS = { to: 'to', occurredOn: 'occurredOn', amount: 'amount', note: 'note' } as const;
const FALLBACK: Record<string, 'occurredOn' | 'amount' | 'to'> = {
  'core.date_in_future': 'occurredOn', 'core.amount_not_positive': 'amount', 'core.currency_mismatch': 'amount',
  'investments.transfer_same_envelope': 'to', 'investments.transfer_currency_mismatch': 'to',
};

/**
 * 2B2b design: a transfer from `source` to another open envelope of the same currency that the person can write
 * (the server checks all of it again: a destination it hides answers 404). A modal dialog in a portal, on
 * the `NewProviderDialog` pattern; the page owns its open state, the focus going back to its button, and the
 * announcement of the success (`onDone`), so a dialog closed by Escape or « Annuler » announces nothing.
 */
export function TransferDialog({ source, onClose, onDone }: {
  source: EnvelopeDetail; onClose: () => void; onDone: (message: string) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const envelopes = $api.useQuery('get', ENVELOPES, { params: { query: { scope: 'me' } } });

  const destinations = (envelopes.data?.items ?? []).filter(
    (e) => e.id !== source.id && !e.closed && e.canWrite && e.currency === source.currency);

  return (
    <ModalDialog title={t('envelope.transfer.title')} onClose={onClose}>
      {(titleId) => (
        <>
          {envelopes.error ? (
            <>
              <ProblemMessage problem={present(toProblem(envelopes.error))} />
              <CloseRow onClose={onClose} />
          </>
        ) : null}
        {!envelopes.data && !envelopes.error ? (
          <div aria-busy="true" className="h-20 shimmer rounded-md bg-muted"><span className="sr-only">{t('envelope.transfer.loading')}</span></div>
        ) : null}
        {envelopes.data && destinations.length === 0 ? (
          <>
            <p>{t('envelope.transfer.none', { currency: source.currency })}</p>
            <CloseRow onClose={onClose} />
          </>
        ) : null}
        {destinations.length > 0 ? <TransferForm source={source} destinations={destinations} titleId={titleId} onClose={onClose} onDone={onDone} /> : null}
        </>
      )}
    </ModalDialog>
  );
}

function CloseRow({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation();
  return <div className="flex justify-end"><Button type="button" variant="outline" onClick={onClose}>{t('envelope.transfer.close')}</Button></div>;
}

function TransferForm({ source, destinations, titleId, onClose, onDone }: {
  source: EnvelopeDetail; destinations: EnvelopeSummary[]; titleId: string; onClose: () => void; onDone: (message: string) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const schema = z.object({
    to: z.string().min(1, t('edit.required')),
    occurredOn: pastDate(t),
    amount: z.string().regex(POSITIVE_AMOUNT, t('investments_form.positive_amount_format')),
    note: z.string().max(2000, t('investments_form.note_too_long')),
  });
  const form = useForm({
    resolver: zodResolver(schema),
    defaultValues: { to: destinations[0]?.id ?? '', occurredOn: todayIso(), amount: '', note: '' },
  });
  // A destination that vanished (404, then the refreshed list) is replaced by the first one left.
  const first = destinations[0]?.id ?? '';
  const chosen = form.watch('to');
  useEffect(() => {
    if (!destinations.some((d) => d.id === chosen)) form.setValue('to', first);
  }, [destinations, chosen, first, form]);
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
        onError: (error) => {
          if (toProblem(error).status === 404) void queryClient.invalidateQueries({ queryKey: ['get', ENVELOPES] });
          placeFieldError(error, t, form.setError, errors, FALLBACK);
        },
      },
    );
  });

  const e = form.formState.errors;
  const unavailable = transfer.error && toProblem(transfer.error).status === 404;
  // The server's 409 does not say which side is closed.
  const problem = errors.problem?.kind === 'conflict'
    ? { ...presentConflict(t, transfer.error), detail: t('envelope.transfer.closed') } : errors.problem;

  return (
    <form aria-labelledby={titleId} noValidate className="space-y-3" onSubmit={(ev) => { ev.stopPropagation(); void submit(ev); }}>
      {unavailable ? <p role="alert" className="text-sm text-destructive">{t('envelope.transfer.unavailable')}</p> : null}
      {!unavailable && problem ? (
        <>
          <ProblemMessage problem={problem} />
          {problem.kind === 'conflict' ? <Reload onReload={() => invalidateInvestments(queryClient)} /> : null}
        </>
      ) : null}
      <div className="grid gap-3 sm:grid-cols-2">
        <div className="sm:col-span-2">
          <Controller control={form.control} name="to" render={({ field }) => (
            <Field label={t('envelope.transfer.to')} error={e.to?.message}>
              {(c) => <SelectField {...c} value={field.value} onValueChange={field.onChange}
                options={destinations.map((d) => ({ value: d.id, label: d.displayName }))} />}
            </Field>
          )} />
        </div>
        <Controller control={form.control} name="occurredOn" render={({ field }) => (
          <Field label={t('envelope.date')} error={e.occurredOn?.message}>
            {(c) => <DateField {...c} max={todayIso()} value={field.value} onChange={field.onChange} />}
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
        <Button type="button" variant="outline" onClick={onClose}>{t('envelope.cancel')}</Button>
        <Button type="submit" disabled={transfer.isPending}>{t('envelope.transfer.submit')}</Button>
      </div>
    </form>
  );
}

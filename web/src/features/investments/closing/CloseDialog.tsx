import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useId, useRef } from 'react';
import { createPortal } from 'react-dom';
import { useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';
import { useApi } from '@/api/ApiProvider';
import { toProblem } from '@/api/errors';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { ISO_DATE, toWireAmount } from '../../subscriptions/edits/amount';
import { Field } from '../../subscriptions/edits/Field';
import { Reload } from '../../subscriptions/edits/Reload';
import { fieldErrorsOf } from '../../subscriptions/fieldErrors';
import { useSubmitErrors } from '../../subscriptions/edits/useSubmitErrors';
import { todayIso } from '../../subscriptions/today';
import { CLOSE, invalidateInvestments, type EnvelopeDetail } from '../api';
import { placeFieldError, POSITIVE_AMOUNT, presentConflict } from '../entries/entries';

const input = 'rounded-md border px-3 py-2';
const PATHS = { closedOn: 'closedOn', 'finalWithdrawal.amount': 'finalWithdrawal' } as const;

/**
 * 2B2b design: closing an envelope at a date, with an optional last withdrawal. A native modal dialog in a portal
 * (the `TransferDialog` pattern); the page owns its open state, the focus and the announcement of the success
 * (`onDone`), so a dialog closed by Escape or « Annuler » announces nothing.
 */
export function CloseDialog({ envelope, onClose, onDone }: {
  envelope: EnvelopeDetail; onClose: () => void; onDone: (message: string) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const ref = useRef<HTMLDialogElement>(null);
  const titleId = useId();

  useEffect(() => {
    const dialog = ref.current;
    if (dialog && !dialog.open) dialog.showModal();
    return () => { if (dialog?.open) dialog.close(); };
  }, []);

  const schema = z.object({
    closedOn: z.string().regex(ISO_DATE, t('edit.required')),
    finalWithdrawal: z.string().regex(new RegExp(`^$|${POSITIVE_AMOUNT.source}`), t('investments_form.positive_amount_format')),
  });
  const form = useForm({ resolver: zodResolver(schema), defaultValues: { closedOn: todayIso(), finalWithdrawal: '' } });
  const errors = useSubmitErrors(form.setError, PATHS);
  // Mutate-level callbacks: react-query drops them once this form unmounted, so a closed dialog never announces.
  const close = $api.useMutation('post', CLOSE, { onSuccess: () => { void invalidateInvestments(queryClient); } });

  const submit = form.handleSubmit((values) => {
    errors.clear();
    close.mutate(
      {
        params: { path: { id: envelope.id } },
        body: {
          closedOn: values.closedOn,
          ...(values.finalWithdrawal ? { finalWithdrawal: { amount: toWireAmount(values.finalWithdrawal), currency: envelope.currency } } : {}),
        },
      },
      {
        onSuccess: () => onDone(t('envelope.closing.closed')),
        onError: (error) => {
          const problem = toProblem(error);
          // The server names `closedOn` in metadata only, not in `errors[]`: the code is the one thing to go by.
          if (problem.status === 422 && problem.code === 'investments.invalid_envelope' && fieldErrorsOf(error).length === 0) {
            form.setError('closedOn', { type: 'server', message: t('envelope.closing.too_early') });
            errors.clear();
          } else {
            placeFieldError(error, t, form.setError, errors, { 'core.date_in_future': 'closedOn', 'core.amount_not_positive': 'finalWithdrawal', 'core.currency_mismatch': 'finalWithdrawal' });
          }
        },
      },
    );
  });

  const e = form.formState.errors;
  const problem = errors.problem?.kind === 'conflict' ? presentConflict(t, close.error) : errors.problem;

  return createPortal(
    <dialog ref={ref} aria-labelledby={titleId}
      onCancel={(event) => { event.preventDefault(); onClose(); }}
      onKeyDown={(event) => { if (event.key === 'Escape') { event.preventDefault(); onClose(); } }}
      className="m-auto w-full max-w-md rounded-md border bg-background p-4 text-foreground backdrop:bg-black/50">
      <div className="space-y-3">
        <h2 id={titleId} className="text-lg font-semibold">{t('envelope.closing.title')}</h2>
        <form aria-labelledby={titleId} noValidate className="space-y-3" onSubmit={(ev) => { ev.stopPropagation(); void submit(ev); }}>
          {problem ? (
            <>
              <ProblemMessage problem={problem} />
              {problem.kind === 'conflict' ? <Reload onReload={() => invalidateInvestments(queryClient)} /> : null}
            </>
          ) : null}
          <p>{t('envelope.closing.effect')}</p>
          <Field label={t('envelope.closing.date')} error={e.closedOn?.message}>
            {(c) => <input type="date" max={todayIso()} {...c} {...form.register('closedOn')} className={input} />}
          </Field>
          <Field label={t('envelope.closing.withdrawal')} hint={t('envelope.closing.withdrawal_help')} error={e.finalWithdrawal?.message}>
            {(c) => <input inputMode="decimal" {...c} {...form.register('finalWithdrawal')} className={`w-32 ${input}`} />}
          </Field>
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" onClick={onClose}>{t('envelope.cancel')}</Button>
            <Button type="submit" disabled={close.isPending}>{t('envelope.closing.submit')}</Button>
          </div>
        </form>
      </div>
    </dialog>,
    document.body,
  );
}

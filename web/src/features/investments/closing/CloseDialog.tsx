import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { Controller, useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';
import { useApi } from '@/api/ApiProvider';
import { toProblem } from '@/api/errors';
import { DateField } from '@/components/form/DateField';
import { ModalDialog } from '@/components/ModalDialog';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { toWireAmount } from '../../subscriptions/edits/amount';
import { Field } from '@/components/form/Field';
import { Reload } from '../../subscriptions/edits/Reload';
import { useSubmitErrors } from '../../subscriptions/edits/useSubmitErrors';
import { todayIso } from '../../subscriptions/today';
import { CLOSE, invalidateInvestments, type EnvelopeDetail } from '../api';
import { pastDate, placeFieldError, POSITIVE_AMOUNT, presentConflict } from '../entries/entries';
import '@/locales/investments';

const PATHS = { closedOn: 'closedOn', 'finalWithdrawal.amount': 'finalWithdrawal' } as const;

/**
 * 2B2b design: closing an envelope at a date, with an optional last withdrawal. A modal dialog in a portal
 * (the `TransferDialog` pattern); the page owns its open state, the focus and the announcement of the success
 * (`onDone`), so a dialog closed by Escape or « Annuler » announces nothing.
 */
export function CloseDialog({ envelope, onClose, onDone }: {
  envelope: EnvelopeDetail; onClose: () => void; onDone: (message: string) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();

  const schema = z.object({
    closedOn: pastDate(t),
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
          // The server names `closedOn` too, but a generic « not accepted » would hide the reason: this code on this form means « too early », with or without `errors[]`.
          if (problem.status === 422 && problem.code === 'investments.invalid_envelope') {
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

  return (
    <ModalDialog title={t('envelope.closing.title')} onClose={onClose}>
      {(titleId) => (
        <>
          <form aria-labelledby={titleId} noValidate className="space-y-3" onSubmit={(ev) => { ev.stopPropagation(); void submit(ev); }}>
            {problem ? (
              <>
                <ProblemMessage problem={problem} />
                {problem.kind === 'conflict' ? <Reload onReload={() => invalidateInvestments(queryClient)} /> : null}
            </>
          ) : null}
          <p>{t('envelope.closing.effect')}</p>
          <div className="grid gap-3 sm:grid-cols-2">
            <Controller control={form.control} name="closedOn" render={({ field }) => (
              <Field label={t('envelope.closing.date')} error={e.closedOn?.message}>
                {(c) => <DateField {...c} max={todayIso()} value={field.value} onChange={field.onChange} />}
              </Field>
            )} />
            <Field label={t('envelope.closing.withdrawal')} hint={t('envelope.closing.withdrawal_help')} error={e.finalWithdrawal?.message}>
              {(c) => <Input inputMode="decimal" {...c} {...form.register('finalWithdrawal')} />}
            </Field>
          </div>
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" onClick={onClose}>{t('envelope.cancel')}</Button>
            <Button type="submit" disabled={close.isPending}>{t('envelope.closing.submit')}</Button>
          </div>
        </form>
        </>
      )}
    </ModalDialog>
  );
}

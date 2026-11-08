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
import { placeFieldError, presentConflict } from '@/components/form/fieldErrors';
import { ISO_DATE } from '@/lib/amount';
import { Field } from '@/components/form/Field';
import { Reload } from '@/components/form/Reload';
import { useSubmitErrors } from '@/components/form/useSubmitErrors';
import { todayIso } from '@/lib/today';
import { CLOSE_SOURCE, invalidateIncome, type IncomeSourceDetail } from '../api';
import '@/locales/income';
import '@/locales/forms';

const PATHS = { endedOn: 'endedOn' } as const;

/**
 * Spec §6: ending a source at a date, in a modal dialog (the envelope's `CloseDialog` pattern). The page owns
 * its open state, the focus and the announcement of the success, so a dialog closed by Escape or « Annuler » announces
 * nothing.
 */
export function EndDialog({ source, onClose, onDone }: {
  source: IncomeSourceDetail; onClose: () => void; onDone: (message: string) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();

  const today = todayIso();
  const schema = z.object({
    endedOn: z.string().regex(ISO_DATE, t('edit.required')).refine((d) => d <= today, t('field_error.core_date_in_future')),
  });
  const form = useForm({ resolver: zodResolver(schema), defaultValues: { endedOn: today } });
  const errors = useSubmitErrors(form.setError, PATHS);
  // Mutate-level callbacks: react-query drops them once this form unmounted, so a closed dialog never announces.
  const end = $api.useMutation('post', CLOSE_SOURCE, { onSuccess: () => { void invalidateIncome(queryClient); } });

  const submit = form.handleSubmit((values) => {
    errors.clear();
    end.mutate({ params: { path: { id: source.id } }, body: { endedOn: values.endedOn } }, {
      onSuccess: () => onDone(t('income_detail.ending.done')),
      onError: (error) => {
        const problem = toProblem(error);
        // On this form `income.invalid_source` means a phase starts after the date: say so, not « not accepted ».
        if (problem.status === 422 && problem.code === 'income.invalid_source') {
          form.setError('endedOn', { type: 'server', message: t('income_detail.ending.too_early') });
          errors.clear();
        } else {
          placeFieldError(error, t, form.setError, errors, { 'core.date_in_future': 'endedOn' });
        }
      },
    });
  });

  const e = form.formState.errors;
  const problem = errors.problem?.kind === 'conflict' ? presentConflict(t, end.error) : errors.problem;

  return (
    <ModalDialog title={t('income_detail.ending.title')} onClose={onClose}>
      {(titleId) => (
        <form aria-labelledby={titleId} noValidate className="space-y-3" onSubmit={(ev) => { ev.stopPropagation(); void submit(ev); }}>
          {problem ? (
            <>
              <ProblemMessage problem={problem} />
              {problem.kind === 'conflict' ? <Reload onReload={() => invalidateIncome(queryClient)} /> : null}
            </>
          ) : null}
          <p>{t('income_detail.ending.effect')}</p>
          <Controller control={form.control} name="endedOn" render={({ field }) => (
            <Field label={t('income_detail.ending.date')} error={e.endedOn?.message}>
              {(c) => <DateField {...c} value={field.value} onChange={field.onChange} max={today} />}
            </Field>
          )} />
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" onClick={onClose}>{t('income_detail.cancel')}</Button>
            <Button type="submit" disabled={end.isPending}>{t('income_detail.ending.submit')}</Button>
          </div>
        </form>
      )}
    </ModalDialog>
  );
}

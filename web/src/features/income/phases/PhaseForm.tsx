import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { useId } from 'react';
import { useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { presentConflict } from '../../investments/entries/entries';
import { Reload } from '../../subscriptions/edits/Reload';
import { useSubmitErrors } from '../../subscriptions/edits/useSubmitErrors';
import { invalidateIncome, PHASE, PHASES, type IncomePhase, type IncomeSourceDetail } from '../api';
import { phaseFieldOf, phaseSchema, toWirePhase, type PhaseValues } from './phase';
import { PhaseFields } from './PhaseFields';

/**
 * Adding a phase (`editing` absent: POST) or correcting one inline on its row (PUT with the `version` it read), spec
 * §7. `onDone` gets the confirmation on success — announced and focused by the page, since the row is about to change
 * — or nothing when « Annuler » abandons.
 */
export function PhaseForm({ source, editing, initial, onDone }: {
  source: IncomeSourceDetail; editing?: IncomePhase; initial: PhaseValues; onDone: (message?: string) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const headingId = useId();
  const form = useForm<PhaseValues>({ resolver: zodResolver(phaseSchema(t)), defaultValues: initial });
  const errors = useSubmitErrors(form.setError, phaseFieldOf);
  const add = $api.useMutation('post', PHASES);
  const correct = $api.useMutation('put', PHASE);

  const submit = form.handleSubmit((values) => {
    errors.clear();
    const body = toWirePhase(values, source.currency);
    const callbacks = {
      onSuccess: () => {
        void invalidateIncome(queryClient);
        onDone(editing ? t('income_detail.phase_corrected') : t('income_detail.phase_added'));
      },
      onError: (error: unknown) => errors.onError(error),
    };
    if (editing) correct.mutate({ params: { path: { id: source.id, phaseId: editing.id } }, body: { ...body, version: editing.version } }, callbacks);
    else add.mutate({ params: { path: { id: source.id } }, body }, callbacks);
  });

  // A conflict reads its own sentence (`field_error.income_phase_conflict`), never the generic one alone.
  const problem = errors.problem?.kind === 'conflict' ? presentConflict(t, editing ? correct.error : add.error) : errors.problem;
  const title = editing ? t('income_detail.correct_phase') : t('income_detail.add_phase');

  return (
    <form aria-labelledby={headingId} noValidate onSubmit={(ev) => void submit(ev)} className="max-w-md space-y-3">
      <h3 id={headingId} className="font-semibold">{title}</h3>
      {problem ? (
        <>
          <ProblemMessage problem={problem} />
          {problem.kind === 'conflict' ? <Reload onReload={() => invalidateIncome(queryClient)} /> : null}
        </>
      ) : null}
      <PhaseFields form={form} prefix="" errors={form.formState.errors} />
      <div className="flex items-center gap-2">
        <Button type="submit" disabled={add.isPending || correct.isPending}>{editing ? t('income_detail.save') : t('income_detail.add_phase_submit')}</Button>
        <Button type="button" variant="ghost" onClick={() => onDone()}>{t('income_detail.cancel')}</Button>
      </div>
    </form>
  );
}

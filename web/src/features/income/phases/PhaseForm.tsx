import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
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
import '@/locales/income';

/**
 * Adding a phase (`editing` absent: POST) or correcting one (PUT with the `version` it read), spec §7, in a dialog.
 * `onDone` gets the confirmation on success — announced and focused by the page, since the row is about to change — or
 * nothing when « Annuler » abandons.
 */
export function PhaseForm({ source, editing, initial, titleId, onDone }: {
  source: IncomeSourceDetail; editing?: IncomePhase; initial: PhaseValues; titleId: string; onDone: (message?: string) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
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

  return (
    <form aria-labelledby={titleId} noValidate onSubmit={(ev) => { ev.stopPropagation(); void submit(ev); }} className="space-y-3">
      {problem ? (
        <>
          <ProblemMessage problem={problem} />
          {problem.kind === 'conflict' || problem.kind === 'not_found' ? <Reload onReload={() => invalidateIncome(queryClient)} /> : null}
        </>
      ) : null}
      <PhaseFields form={form} prefix="" errors={form.formState.errors} />
      <div className="flex justify-end gap-2">
        <Button type="button" variant="outline" onClick={() => onDone()}>{t('income_detail.cancel')}</Button>
        <Button type="submit" disabled={add.isPending || correct.isPending}>{editing ? t('income_detail.save') : t('income_detail.add_phase_submit')}</Button>
      </div>
    </form>
  );
}

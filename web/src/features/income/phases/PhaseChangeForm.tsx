import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { Controller, useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem, type Presentation } from '@/api/errors';
import { SelectField } from '@/components/form/SelectField';
import { formatLocalDate } from '@/components/LocalDate';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { presentConflict } from '../../investments/entries/entries';
import { Field } from '@/components/form/Field';
import { Reload } from '../../subscriptions/edits/Reload';
import { useSubmitErrors } from '../../subscriptions/edits/useSubmitErrors';
import { fieldErrorsOf, knownConflictMessage } from '../../subscriptions/fieldErrors';
import { todayIso } from '../../subscriptions/today';
import { EXPECTED, invalidateIncome, PHASE_CHANGES, type IncomePhase, type IncomeSourceDetail } from '../api';
import { defaultEffectiveDate, paymentDates, paymentWindow } from '../expected/months';
import { phaseChangeFieldOf, phaseChangeSchema, phaseChangeValuesOf, toWirePhaseChange, type PhaseChangeValues } from './phase';
import { PhaseFields } from './PhaseFields';

/**
 * Spec §3: a change of amount, in a dialog. The effective date is chosen among the phase's own payment dates over 24
 * months, read from the expected income, so a date the server would refuse cannot be picked. `onDone` gets the
 * confirmation (announced and focused by the page) or nothing on « Annuler ».
 */
export function PhaseChangeForm({ source, phase, titleId, onDone }: {
  source: IncomeSourceDetail; phase: IncomePhase; titleId: string; onDone: (message?: string) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const answer = $api.useQuery('get', EXPECTED, { params: { path: { id: source.id }, query: paymentWindow(todayIso()) } });
  const dates = answer.data ? paymentDates(answer.data, phase) : [];
  const cancel = <Button type="button" variant="outline" onClick={() => onDone()}>{t('income_detail.cancel')}</Button>;

  if (!answer.data) {
    return answer.error
      ? <div className="space-y-3"><ProblemMessage problem={present(toProblem(answer.error))} /><div className="flex justify-end">{cancel}</div></div>
      : <div role="status" aria-busy="true" aria-label={t('income_detail.change.loading')} className="h-24 animate-pulse rounded-md border bg-muted" />;
  }
  if (dates.length === 0) {
    return (
      <form aria-labelledby={titleId} className="space-y-3" onSubmit={(ev) => ev.preventDefault()}>
        <p>{t('income_detail.change.no_dates')}</p>
        <div className="flex justify-end">{cancel}</div>
      </form>
    );
  }
  return <ChangeForm source={source} phase={phase} dates={dates} labelledBy={titleId} onDone={onDone} />;
}

function ChangeForm({ source, phase, dates, labelledBy, onDone }: {
  source: IncomeSourceDetail; phase: IncomePhase; dates: string[]; labelledBy: string; onDone: (message?: string) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const form = useForm<PhaseChangeValues>({
    resolver: zodResolver(phaseChangeSchema(t)),
    defaultValues: phaseChangeValuesOf(phase, defaultEffectiveDate(dates, todayIso()) ?? ''),
  });
  const errors = useSubmitErrors(form.setError, phaseChangeFieldOf);
  const change = $api.useMutation('post', PHASE_CHANGES);

  const submit = form.handleSubmit((values) => {
    errors.clear();
    change.mutate({ params: { path: { id: source.id } }, body: toWirePhaseChange(phase.label, values, source.currency) }, {
      onSuccess: () => { void invalidateIncome(queryClient); onDone(t('income_detail.change.done')); },
      onError: (error) => {
        errors.onError(error);
        // The server names the previous and the next payment in English; the select only offers payment dates anyway.
        if (fieldErrorsOf(error).some((f) => f.path === 'effectiveOn' && f.code === 'income.invalid_phase'))
          form.setError('effectiveOn', { type: 'server', message: t('income_detail.change.not_a_payment') });
      },
    });
  });

  const e = form.formState.errors;
  // Every refusal reads in French: a conflict by its own sentence, a 422 no field owns by its code's.
  const problem: Presentation | null =
    errors.problem?.kind === 'conflict' ? presentConflict(t, change.error)
      : errors.problem?.kind === 'validation' ? { ...errors.problem, detail: knownConflictMessage(t, toProblem(change.error).code) }
        : errors.problem;

  return (
    <form aria-labelledby={labelledBy} noValidate onSubmit={(ev) => { ev.stopPropagation(); void submit(ev); }} className="space-y-3">
      {problem ? (
        <>
          <ProblemMessage problem={problem} />
          {problem.kind === 'conflict' || problem.kind === 'not_found' ? <Reload onReload={() => invalidateIncome(queryClient)} /> : null}
        </>
      ) : null}
      <Controller control={form.control} name="effectiveOn" render={({ field }) => (
        <Field label={t('income_detail.change.effective_on')} error={e.effectiveOn?.message}>
          {(c) => <SelectField {...c} value={field.value} onValueChange={field.onChange} options={dates.map((d) => ({ value: d, label: formatLocalDate(d) }))} />}
        </Field>
      )} />
      <PhaseFields form={form} prefix="" errors={e} fields={['netAmount', 'cadence', 'grossAmount', 'note']} />
      <div className="flex justify-end gap-2">
        <Button type="button" variant="outline" onClick={() => onDone()}>{t('income_detail.cancel')}</Button>
        <Button type="submit" disabled={change.isPending}>{t('income_detail.change.submit')}</Button>
      </div>
    </form>
  );
}

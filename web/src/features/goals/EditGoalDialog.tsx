import { zodResolver } from '@hookform/resolvers/zod';
import { useEffect, useRef } from 'react';
import { useForm, type Resolver } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { present, toProblem } from '@/api/errors';
import { FormDialog } from '@/components/FormDialog';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { useEditGoal, useGoal, type GoalDetail } from './api';
import { FundingFields, useEnvelopeOptions } from './FundingFields';
import { GoalFields } from './GoalFields';
import { goalDefaults, goalSchema, toEditBody, type GoalValues } from './goalSchema';
import { useGoalErrors } from './useGoalErrors';
import '@/locales/goals';

/**
 * The goal's name, target, date, note and funding (its kind, owners and visibility do not change). The server keeps the
 * links to envelopes the editor does not see in scope `me` and drops the others missing from the body, so the form reads
 * its own copy of the goal in scope `me` whatever the page's scope (household hides the editor's private envelopes).
 */
export function EditGoalDialog({ goalId, onClose, onDone }: { goalId: string; onClose: () => void; onDone: (message: string) => void }) {
  const { t } = useTranslation();
  const answer = useGoal(goalId, 'me');
  return (
    <FormDialog title={t('goals_form.edit_title')} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => answer.data ? <EditForm detail={answer.data} titleId={titleId} done={done} />
        : answer.error ? <ProblemMessage problem={present(toProblem(answer.error))} />
        : <div aria-busy="true" aria-label={t('goals_detail.loading')} className="h-40 shimmer rounded-md border bg-muted" />}
    </FormDialog>
  );
}

function EditForm({ detail, titleId, done }: { detail: GoalDetail; titleId: string; done: (message?: string) => void }) {
  const { t } = useTranslation();
  const edit = useEditGoal();
  const options = useEnvelopeOptions(detail.funding);
  const schema = goalSchema(t, { withOwners: false, keepDate: detail.goal.targetDate ?? undefined, hiddenLinks: detail.goal.partial });
  const form = useForm<GoalValues>({
    resolver: zodResolver(schema) as unknown as Resolver<GoalValues>,
    defaultValues: { ...goalDefaults(detail), visibility: 'shared', owners: [] },
  });
  const errors = useGoalErrors(form.setError);
  // A conflict refetches the goal: the form takes the fresh values with the fresh version, so a retry never overwrites them blindly.
  const seen = useRef(detail.version);
  useEffect(() => {
    if (detail.version === seen.current) return;
    seen.current = detail.version;
    form.reset({ ...goalDefaults(detail), visibility: 'shared', owners: [] });
    // eslint-disable-next-line react-hooks/exhaustive-deps -- the version is the event.
  }, [detail.version]);

  return (
    <form aria-labelledby={titleId} noValidate className="space-y-3" onSubmit={(event) => {
      event.stopPropagation();
      void form.handleSubmit((values) => {
        errors.clear();
        // The version of the goal as the form now holds it: a refetch after a conflict hands over the new one.
        edit.mutate({ params: { path: { id: detail.goal.id } }, body: toEditBody(values, detail.version) },
          { onSuccess: () => done(t('goals_form.saved')), onError: (error) => errors.onError(error) });
      })(event);
    }}>
      {errors.stale ? <p role="alert" className="rounded-md border border-destructive p-3">{t('goals_form.conflict')}</p> : null}
      {errors.problem ? <ProblemMessage problem={errors.problem} /> : null}
      <GoalFields form={form} withKind={false} />
      <FundingFields form={form} options={options} />
      <div className="flex justify-end gap-2">
        <Button type="button" variant="outline" onClick={() => done()}>{t('goals_form.cancel')}</Button>
        <Button type="submit" disabled={edit.isPending}>{t('goals_form.save')}</Button>
      </div>
    </form>
  );
}

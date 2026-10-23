import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { FormProvider, useForm, type Path } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { FormDialog } from '@/components/FormDialog';
import { StepForm } from '@/components/page/StepForm';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { fieldErrorsOf } from '../subscriptions/fieldErrors';
import { useSubmitErrors } from '../subscriptions/edits/useSubmitErrors';
import { SCENARIO, SCENARIO_DEFAULTS, SCENARIOS, type Scenario } from './api';
import { draftPath, firstStepOf, fromDocument, ScenarioFields, STEP_FIELDS, toDocument, type ScenarioDraft, type ScenarioStep } from './new/ScenarioFields';

/**
 * Spec §7: the saved scenario in the creation form's steps, all of them (« Modifier le scénario ») or the assumptions alone
 * (« Hypothèses »); either way the whole document is PUT, and a refused field lands on its own step.
 */
export function AssumptionsDialog({ scenario, steps, title, onClose, onDone }: {
  scenario: Scenario; steps: ScenarioStep[]; title: string; onClose: () => void; onDone: (message: string) => void;
}) {
  return (
    <FormDialog title={title} onClose={onClose} onDone={onDone}>
      {({ done }) => <EditForm scenario={scenario} steps={steps} done={done} />}
    </FormDialog>
  );
}

function EditForm({ scenario, steps, done }: { scenario: Scenario; steps: ScenarioStep[]; done: (message?: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const form = useForm<ScenarioDraft>({ defaultValues: fromDocument(scenario) });
  const scope = scenario.scope === 'me' ? 'me' : 'household';
  // The envelopes the funding step offers; the assumptions alone need none.
  const defaults = $api.useQuery('get', SCENARIO_DEFAULTS, { params: { query: { scope } } }, { enabled: steps.includes('funding') });
  const [reveal, setReveal] = useState<{ step: string } | null>(null);
  const errors = useSubmitErrors(form.setError, draftPath);
  const save = $api.useMutation('put', SCENARIO, {
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['get', SCENARIO] });
      void queryClient.invalidateQueries({ queryKey: ['get', SCENARIOS] });
      done(steps.length === 1 ? t('scenarios.detail.assumptions_saved') : t('scenarios.detail.saved'));
    },
    onError: (error) => {
      errors.onError(error);
      const step = firstStepOf(fieldErrorsOf(error).flatMap((f) => draftPath(f.path) ?? []));
      setReveal(step && steps.includes(step) ? { step } : null);
    },
  });

  return (
    <div className="space-y-4">
      {errors.problem ? <ProblemMessage problem={errors.problem} /> : null}
      <FormProvider {...form}>
        <StepForm
          trigger={(fields) => form.trigger(fields as Path<ScenarioDraft>[])}
          onSubmit={() => void form.handleSubmit((v) => {
            errors.clear();
            save.mutate({
              params: { path: { id: scenario.id } },
              body: { name: v.name.trim(), visibility: v.visibility, scope, document: toDocument(v) },
            });
          })()}
          submitLabel={t('scenarios.detail.save')} pending={save.isPending} reveal={reveal}
          steps={steps.map((id) => ({
            id, title: t(`scenarios.form.step_${id}`), fields: STEP_FIELDS[id],
            content: <ScenarioFields step={id} holdings={defaults.data?.holdings ?? []} />,
          }))}
        />
      </FormProvider>
      <Button type="button" variant="ghost" onClick={() => done()}>{t('common.cancel')}</Button>
    </div>
  );
}

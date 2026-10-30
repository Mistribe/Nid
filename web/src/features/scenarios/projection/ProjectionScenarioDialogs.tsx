import { FormProvider, useForm, type Path } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { Field } from '@/components/form/Field';
import { FormDialog } from '@/components/FormDialog';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { useSubmitErrors } from '../../subscriptions/edits/useSubmitErrors';
import type { ProjectionScenario } from '../api';
import { AssumptionFields, assumptionPath, assumptionsDraft, HorizonField, overridesOf, type AssumptionsDraft } from './AssumptionFields';
import { bodyOf, useSave } from './save';
import '@/locales/forms';
import '@/locales/scenarios';

export type ScenarioDialog = 'rename' | 'assumptions';

/** Spec §8: « Renommer » (the name) or « Modifier les hypothèses » (horizon and overrides), each saving the whole scenario. */
export default function ProjectionScenarioDialog({ which, scenario, onClose, onDone }: {
  which: ScenarioDialog; scenario: ProjectionScenario; onClose: () => void; onDone: (message: string) => void;
}) {
  const { t } = useTranslation();
  return (
    <FormDialog title={t(which === 'rename' ? 'scenarios.projection.detail.rename_title' : 'scenarios.projection.detail.edit_assumptions')}
      onClose={onClose} onDone={onDone}>
      {({ done }) => (which === 'rename' ? <RenameForm scenario={scenario} done={done} /> : <AssumptionsForm scenario={scenario} done={done} />)}
    </FormDialog>
  );
}

function RenameForm({ scenario, done }: { scenario: ProjectionScenario; done: (message?: string) => void }) {
  const { t } = useTranslation();
  const form = useForm<{ name: string }>({ defaultValues: { name: scenario.name } });
  const errors = useSubmitErrors(form.setError, (path) => (path === 'name' ? 'name' : undefined));
  const save = useSave(scenario, () => done(t('scenarios.projection.detail.renamed')), errors.onError);
  const name = form.formState.errors.name?.message;
  return (
    <form noValidate className="space-y-4" onSubmit={(e) => void form.handleSubmit((v) => {
      errors.clear();
      save.mutate({ params: { path: { id: scenario.id } }, body: bodyOf(scenario, { name: v.name.trim() }) });
    })(e)}>
      {errors.problem ? <ProblemMessage problem={errors.problem} /> : null}
      <Field label={t('scenarios.form.name')} error={name}>
        {(c) => <Input {...c} {...form.register('name', { validate: (v) => v.trim() !== '' || t('edit.required') })} />}
      </Field>
      <Actions pending={save.isPending} cancel={() => done()} />
    </form>
  );
}

type AssumptionsValues = { horizon: string; assumptions: AssumptionsDraft };

function AssumptionsForm({ scenario, done }: { scenario: ProjectionScenario; done: (message?: string) => void }) {
  const { t } = useTranslation();
  const doc = scenario.result.document;
  const form = useForm<AssumptionsValues>({ defaultValues: { horizon: doc.horizon ?? scenario.result.scenario.horizon, assumptions: assumptionsDraft(doc.assumptions) } });
  const errors = useSubmitErrors(form.setError, (path) => assumptionPath(path) as Path<AssumptionsValues> | undefined);
  const save = useSave(scenario, () => done(t('scenarios.projection.detail.assumptions_saved')), errors.onError);
  return (
    <form noValidate className="space-y-4" onSubmit={(e) => void form.handleSubmit((v) => {
      errors.clear();
      const assumptions = overridesOf(v.assumptions);
      // No override at all is no `assumptions`: the household's.
      save.mutate({
        params: { path: { id: scenario.id } },
        body: bodyOf(scenario, { document: { version: doc.version, horizon: v.horizon, ...(assumptions ? { assumptions } : {}), events: doc.events } }),
      });
    })(e)}>
      {errors.problem ? <ProblemMessage problem={errors.problem} /> : null}
      <p className="text-sm text-muted-foreground">{t('scenarios.projection.detail.assumptions_intro')}</p>
      <FormProvider {...form}>
        <HorizonField base={scenario.result.base} />
        <AssumptionFields base={scenario.result.base} />
      </FormProvider>
      <Actions pending={save.isPending} cancel={() => done()} />
    </form>
  );
}

function Actions({ pending, cancel }: { pending: boolean; cancel: () => void }) {
  const { t } = useTranslation();
  return (
    <div className="flex flex-wrap justify-end gap-2">
      <Button type="button" variant="ghost" onClick={cancel}>{t('common.cancel')}</Button>
      <Button type="submit" disabled={pending}>{t('scenarios.projection.detail.save')}</Button>
    </div>
  );
}

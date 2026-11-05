import { keepPreviousData, useQueryClient } from '@tanstack/react-query';
import { useNavigate, useSearch } from '@tanstack/react-router';
import { lazy, Suspense, useEffect, useId, useState } from 'react';
import { FormProvider, useForm, useWatch, type Path } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { useSession } from '@/app/Providers';
import type { NewScenarioSearch, RootSearch } from '@/app/router';
import { PageHeader } from '@/components/page/PageHeader';
import { StepForm } from '@/components/page/StepForm';
import { ProblemMessage } from '@/components/ProblemMessage';
import { fieldErrorsOf } from '../../subscriptions/fieldErrors';
import { useSubmitErrors } from '../../subscriptions/edits/useSubmitErrors';
import { SCENARIO_DEFAULTS, SCENARIO_SIMULATE, SCENARIOS, type PropertyPurchaseDoc, type ScenarioDefaults } from '../api';
import { CapacitySummary } from './CapacitySummary';
import { draftPath, firstStepOf, newDraft, ScenarioFields, STEP_FIELDS, toDocument, type ScenarioDraft, type ScenarioStep } from './ScenarioFields';
import '@/locales/scenarios';

// A projection's creation loads on its own (the projection spec §8): a purchase never fetches its forms and chart.
const NewProjectionScenarioPage = lazy(() => import('../projection/routes').then((m) => ({ default: m.NewProjectionScenarioPage })));
const NewSaleScenarioPage = lazy(() => import('../sale/NewSaleScenarioPage').then((m) => ({ default: m.NewSaleScenarioPage })));

/** `/scenarios/new`: a purchase scenario, or a projection one with `?kind=projection`, a sale with `?kind=sale`. */
export function NewScenarioPage() {
  const search: NewScenarioSearch = useSearch({ strict: false });
  if (search.kind === 'projection') return <Suspense fallback={null}><NewProjectionScenarioPage /></Suspense>;
  if (search.kind === 'sale') return <Suspense fallback={null}><NewSaleScenarioPage /></Suspense>;
  return <NewPurchaseScenarioPage />;
}

/** Spec §7: a scenario created in four steps, its capacity recalculated at the side while it is typed. */
function NewPurchaseScenarioPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const search: RootSearch = useSearch({ strict: false });
  const scope = search.scope === 'me' ? 'me' : 'household';
  const defaults = $api.useQuery('get', SCENARIO_DEFAULTS, { params: { query: { scope } } });
  return (
    <article className="space-y-4">
      <PageHeader crumbs={[{ label: t('scenarios.title'), to: '/scenarios' }]} title={t('scenarios.form.title')} />
      {defaults.error ? <ProblemMessage problem={present(toProblem(defaults.error))} />
        : !defaults.data ? <p aria-busy="true" className="text-sm text-muted-foreground">{t('scenarios.form.loading')}</p>
          : <ScenarioForm defaults={defaults.data} scope={scope} />}
    </article>
  );
}

const STEPS: ScenarioStep[] = ['property', 'funding', 'loans', 'assumptions'];

function ScenarioForm({ defaults, scope }: { defaults: ScenarioDefaults; scope: 'me' | 'household' }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { session } = useSession();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const summaryId = useId();
  const form = useForm<ScenarioDraft>({ defaultValues: newDraft(t, defaults) });

  // The summary follows the typing, 400 ms after the last change; the server answers 422 until the document holds together.
  useWatch({ control: form.control });
  const typed = JSON.stringify(toDocument(form.getValues()));
  const [sent, setSent] = useState(typed);
  useEffect(() => {
    const timer = setTimeout(() => setSent(typed), 400);
    return () => clearTimeout(timer);
  }, [typed]);
  const simulation = $api.useQuery('post', SCENARIO_SIMULATE, { body: { scope, document: JSON.parse(sent) as PropertyPurchaseDoc } },
    { placeholderData: keepPreviousData, retry: false });

  const [reveal, setReveal] = useState<{ step: string } | null>(null);
  const errors = useSubmitErrors(form.setError, draftPath);
  const create = $api.useMutation('post', SCENARIOS, {
    onSuccess: ({ id }) => {
      void queryClient.invalidateQueries({ queryKey: ['get', SCENARIOS] });
      void navigate({ to: '/scenarios/$id', params: { id } });
    },
    onError: (error) => {
      errors.onError(error);
      // The refusal lands on the last step: go back to the first step holding a field it names.
      const step = firstStepOf(fieldErrorsOf(error).flatMap((f) => draftPath(f.path) ?? []));
      setReveal(step ? { step } : null);
    },
  });

  return (
    <div className="grid gap-6 lg:grid-cols-[minmax(0,1fr)_20rem]">
      <div className="space-y-4">
        {errors.problem ? <ProblemMessage problem={errors.problem} /> : null}
        <FormProvider {...form}>
          <StepForm
            trigger={(fields) => form.trigger(fields as Path<ScenarioDraft>[])}
            onSubmit={() => void form.handleSubmit((v) => {
              errors.clear();
              create.mutate({ body: {
                name: v.name.trim(), visibility: v.visibility, scope, owners: [{ personId: session.person.id, shareBp: 10000 }], document: toDocument(v),
              } });
            })()}
            submitLabel={t('scenarios.form.submit')} pending={create.isPending} reveal={reveal}
            steps={STEPS.map((id) => ({
              id, title: t(`scenarios.form.step_${id}`), fields: STEP_FIELDS[id],
              content: <ScenarioFields step={id} holdings={defaults.holdings} />,
            }))}
          />
        </FormProvider>
      </div>
      <aside aria-labelledby={summaryId} className="space-y-2 self-start rounded-md border p-4 lg:sticky lg:top-4">
        <h2 id={summaryId} className="font-semibold">{t('scenarios.summary.title')}</h2>
        {simulation.data ? <CapacitySummary result={simulation.data} />
          : <p className="text-sm text-muted-foreground">{t('scenarios.summary.pending')}</p>}
      </aside>
    </div>
  );
}

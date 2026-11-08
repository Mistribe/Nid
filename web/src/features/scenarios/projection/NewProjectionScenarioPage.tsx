import { keepPreviousData, useQueryClient } from '@tanstack/react-query';
import { useNavigate, useSearch } from '@tanstack/react-router';
import { lazy, Suspense, useEffect, useId, useRef, useState, type ReactNode } from 'react';
import { Controller, FormProvider, useForm, useFormContext, useWatch, type Path } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem, type FieldError, type Presentation } from '@/api/errors';
import { useSession } from '@/app/Providers';
import type { RootSearch } from '@/lib/search';
import { Field } from '@/components/form/Field';
import { Segmented } from '@/components/form/Segmented';
import { formatCurrency } from '@/components/Money';
import { PageHeader } from '@/components/page/PageHeader';
import { StatLine } from '@/components/page/StatLine';
import { StepForm } from '@/components/page/StepForm';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Input } from '@/components/ui/input';
import { Skeleton } from '@/components/ui/skeleton';
import { PROJECTION, type Projection } from '@/api/dashboard';
import { Folded } from '@/components/form/Folded';
import { fieldErrorsOf, messageFor } from '@/components/form/fieldErrors';
import { PROJECTION_SCENARIOS, PROJECTION_SIMULATE, SCENARIOS } from '../api';
import type { ProjectionComparison, ProjectionDocument, ProjectionEvent } from './api';
import { AssumptionFields, assumptionPath, assumptionsDraft, HorizonField, isComplete, overridesOf, type AssumptionsDraft } from './AssumptionFields';
import type { EventFieldError } from './EventDialog';
import { EventList } from './EventList';
import { ProjectionWarnings } from './ProjectionWarnings';
import '@/locales/forms';
import { baseAndScenario } from './chartSeries';
import '@/locales/scenarios';
import { Loading, SectionSkeleton } from '@/components/page/skeletons';

// The chart (Recharts) loads once there is a result to draw.
const ProjectionComparisonChart = lazy(() => import('./ProjectionComparisonChart').then((m) => ({ default: m.ProjectionComparisonChart })));

const STEPS = ['general', 'events', 'assumptions'] as const;
type Step = (typeof STEPS)[number];

/** The scenario as typed (spec §4 and §6): its name, visibility, scope and horizon, then the assumptions it changes (empty: the household's). */
type ProjectionDraft = {
  name: string; visibility: string; scope: 'household' | 'me'; horizon: string;
  assumptions: AssumptionsDraft;
};

const STEP_FIELDS: Record<Step, string[]> = { general: ['name', 'visibility', 'scope', 'horizon'], events: [], assumptions: ['assumptions'] };

const toDocument = (d: ProjectionDraft, events: ProjectionEvent[]): ProjectionDocument => {
  const assumptions = overridesOf(d.assumptions);
  return { version: 1, horizon: d.horizon, ...(assumptions ? { assumptions } : {}), events };
};

/** A server path outside the events (`horizon`, `assumptions.classRates.equity`) as the draft's field. */
function draftPath(path: string): Path<ProjectionDraft> | undefined {
  if (path === 'name' || path === 'visibility' || path === 'scope') return path;
  return assumptionPath(path) as Path<ProjectionDraft> | undefined;
}

const EVENT_PATH = /^events\[(\d+)\](?:\.(.+))?$/;

/** Spec §8: a projection scenario created in three steps, the base and the scenario recomputed at the side while it is typed. */
export function NewProjectionScenarioPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const search: RootSearch = useSearch({ strict: false });
  const scope = search.scope === 'me' ? 'me' : 'household';
  // The base: the retirements the horizon offers, the currency and the household's assumptions the fields fall back to.
  const base = $api.useQuery('get', PROJECTION, { params: { query: { scope } } });
  return (
    <article className="space-y-4">
      <PageHeader crumbs={[{ label: t('scenarios.title'), to: '/scenarios' }]} title={t('scenarios.projection.form.title')} />
      {base.error ? <ProblemMessage problem={present(toProblem(base.error))} />
        : !base.data ? <Loading label={t('scenarios.projection.form.loading')}><SectionSkeleton /><SectionSkeleton /></Loading>
          : <ProjectionForm base={base.data} scope={scope} />}
    </article>
  );
}

function ProjectionForm({ base, scope }: { base: Projection; scope: 'me' | 'household' }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { session } = useSession();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const summaryId = useId();
  const form = useForm<ProjectionDraft>({
    mode: 'onChange',
    defaultValues: {
      name: '', visibility: 'shared', scope, horizon: '10y',
      assumptions: assumptionsDraft(),
    },
  });
  const [events, setEvents] = useState<ProjectionEvent[]>([]);
  const [eventErrors, setEventErrors] = useState<Record<number, EventFieldError[]>>({});
  const [problem, setProblem] = useState<Presentation | null>(null);
  const [reveal, setReveal] = useState<{ step: string } | null>(null);

  // The side panel follows the typing, 400 ms after the last change, and only once every typed figure reads as one.
  const values = useWatch({ control: form.control }) as ProjectionDraft;
  const typed = JSON.stringify({ scope: values.scope, document: toDocument(values, events) });
  const complete = isComplete(values.assumptions);
  const [sent, setSent] = useState(typed);
  useEffect(() => {
    if (!complete) return;
    const timer = setTimeout(() => setSent(typed), 400);
    return () => clearTimeout(timer);
  }, [typed, complete]);
  const simulation = $api.useQuery('post', PROJECTION_SIMULATE, { body: JSON.parse(sent) as { scope: string; document: ProjectionDocument } },
    { placeholderData: keepPreviousData, retry: false });

  /** The server's refusals on their fields: an event's under it (and in its dialog), the others on the form; the rest above it. */
  const place = (error: unknown): FieldError[] => {
    const fields = fieldErrorsOf(error);
    const byEvent: Record<number, EventFieldError[]> = {};
    for (const f of fields) {
      const message = messageFor(t, f);
      const at = EVENT_PATH.exec(f.path);
      if (at) (byEvent[Number(at[1])] ??= []).push({ path: at[2] ?? '', message });
      else {
        const name = draftPath(f.path);
        if (name) form.setError(name, { type: 'server', message });
      }
    }
    setEventErrors(byEvent);
    const placed = fields.length > 0 && fields.every((f) => EVENT_PATH.test(f.path) || draftPath(f.path));
    setProblem(placed ? null : present(toProblem(error)));
    return fields;
  };

  const { data: simulated, error: simulateError } = simulation;
  // The document a save refused: a simulation of that same document, answering late, must not wipe the refusals off it.
  const refused = useRef<string | null>(null);
  useEffect(() => {
    if (simulateError) place(simulateError);
    else if (simulated && sent !== refused.current) { setEventErrors({}); setProblem(null); }
    // eslint-disable-next-line react-hooks/exhaustive-deps -- an answer is the event; `place` changes every render.
  }, [simulated, simulateError]);

  const create = $api.useMutation('post', PROJECTION_SCENARIOS, {
    onSuccess: ({ id }) => {
      void queryClient.invalidateQueries({ queryKey: ['get', SCENARIOS] });
      void navigate({ to: '/scenarios/projection/$id', params: { id } });
    },
    onError: (error) => {
      refused.current = typed;
      const fields = place(error);
      // The refusal lands on the last step: go back to the first step holding a field it names.
      const step = STEPS.find((s) => fields.some((f) => s === 'events' ? EVENT_PATH.test(f.path)
        : STEP_FIELDS[s].some((root) => draftPath(f.path)?.split('.')[0] === root)));
      setReveal(step ? { step } : null);
    },
  });

  // An event changed drops its refusals; a removal drops its own and shifts the ones after it.
  const edit = (next: ProjectionEvent[], changed?: { at: number; removed: boolean }) => {
    setEvents(next);
    if (!changed) return;
    setEventErrors((all) => Object.fromEntries(Object.entries(all).flatMap(([key, list]) => {
      const i = Number(key);
      if (i === changed.at) return [];
      return [[changed.removed && i > changed.at ? i - 1 : i, list]];
    })));
  };

  const content: Record<Step, ReactNode> = {
    general: <GeneralStep base={base} />,
    events: (
      <EventList events={events} errors={eventErrors} currency={base.currency} scope={values.scope}
        onAdd={(e) => edit([...events, e])}
        onEdit={(i, e) => edit(events.map((x, j) => (j === i ? e : x)), { at: i, removed: false })}
        onRemove={(i) => edit(events.filter((_, j) => j !== i), { at: i, removed: true })} />
    ),
    assumptions: <AssumptionsStep base={base} />,
  };

  return (
    <div className="grid gap-6 lg:grid-cols-[minmax(0,1fr)_22rem]">
      <div className="space-y-4">
        {problem ? <ProblemMessage problem={problem} /> : null}
        <FormProvider {...form}>
          <StepForm
            trigger={(fields) => (fields.length === 0 ? Promise.resolve(true) : form.trigger(fields as Path<ProjectionDraft>[]))}
            onSubmit={() => void form.handleSubmit((v) => {
              setProblem(null);
              create.mutate({ body: {
                name: v.name.trim(), visibility: v.visibility, scope: v.scope, owners: [{ personId: session.person.id, shareBp: 10000 }],
                document: toDocument(v, events),
              } });
            })()}
            submitLabel={t('scenarios.projection.form.submit')} pending={create.isPending} reveal={reveal}
            steps={STEPS.map((id) => ({ id, title: t(`scenarios.projection.form.step_${id}`), fields: STEP_FIELDS[id], content: content[id] }))}
          />
        </FormProvider>
      </div>
      <aside aria-labelledby={summaryId} className="space-y-3 self-start rounded-md border p-4 lg:sticky lg:top-4">
        <h2 id={summaryId} className="font-semibold">{t('scenarios.projection.result.title')}</h2>
        {simulated ? <Result result={simulated} /> : <p className="text-sm text-muted-foreground">{t('scenarios.projection.result.pending')}</p>}
      </aside>
    </div>
  );
}

function Result({ result }: { result: ProjectionComparison }) {
  const { t } = useTranslation();
  const currency = result.base.currency;
  return (
    <div className="space-y-3">
      <StatLine items={[
        { label: t('scenarios.projection.result.gap_real'), value: formatCurrency(result.gapReal, currency) },
        { label: t('scenarios.projection.result.gap_nominal'), value: formatCurrency(result.gapNominal, currency) },
      ]} />
      <Suspense fallback={<Skeleton className="h-56 w-full rounded-xl" />}><ProjectionComparisonChart series={baseAndScenario(result, t)} currency={result.base.currency} milestones={result.scenario.milestones} real /></Suspense>
      <ProjectionWarnings warnings={result.scenario.warnings} />
      <p className="text-sm text-muted-foreground">{t('scenarios.projection.result.reminder')}</p>
    </div>
  );
}

function GeneralStep({ base }: { base: Projection }) {
  const { t } = useTranslation();
  const form = useFormContext<ProjectionDraft>();
  const name = form.getFieldState('name', form.formState).error?.message;
  const visibilityId = useId();
  const scopeId = useId();
  return (
    <>
      <Field label={t('scenarios.form.name')} error={name}>
        {(c) => <Input {...c} {...form.register('name', { validate: (v) => v.trim() !== '' || t('edit.required') })} />}
      </Field>
      <div className="space-y-1">
        <span id={visibilityId} className="block text-sm">{t('scenarios.form.visibility')}</span>
        <Controller control={form.control} name="visibility" render={({ field }) => (
          <Segmented aria-labelledby={visibilityId} value={field.value} onValueChange={field.onChange}
            options={(['shared', 'private'] as const).map((v) => ({ value: v, label: t(`scenarios.form.visibility_${v}`) }))} />
        )} />
      </div>
      <div className="space-y-1">
        <span id={scopeId} className="block text-sm">{t('scenarios.projection.form.scope')}</span>
        <Controller control={form.control} name="scope" render={({ field }) => (
          <Segmented aria-labelledby={scopeId} value={field.value} onValueChange={field.onChange}
            options={(['household', 'me'] as const).map((v) => ({ value: v, label: t(`scope.${v}`) }))} />
        )} />
      </div>
      <HorizonField base={base} />
    </>
  );
}

function AssumptionsStep({ base }: { base: Projection }) {
  const { t } = useTranslation();
  const form = useFormContext<ProjectionDraft>();
  return (
    <>
      <p className="text-sm text-muted-foreground">{t('scenarios.projection.form.assumptions_intro')}</p>
      <Folded label={t('scenarios.projection.form.assumptions_more')} hasError={Boolean(form.formState.errors.assumptions)}>
        <AssumptionFields base={base} />
      </Folded>
    </>
  );
}

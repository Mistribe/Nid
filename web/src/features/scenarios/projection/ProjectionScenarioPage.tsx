import { useQueryClient } from '@tanstack/react-query';
import { Link, useNavigate, useParams } from '@tanstack/react-router';
import { Pencil } from 'lucide-react';
import { lazy, Suspense, useEffect, useId, useRef, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem, type Presentation } from '@/api/errors';
import { AccessBadge } from '@/components/AccessBadge';
import { ConfirmFromMenu } from '@/components/ConfirmAction';
import { Segmented } from '@/components/form/Segmented';
import { LocalDate } from '@/components/LocalDate';
import { formatCurrency } from '@/components/Money';
import { DetailTabs, useDetailTab } from '@/components/page/DetailTabs';
import { PageHeader } from '@/components/page/PageHeader';
import { RowMenu } from '@/components/page/RowMenu';
import { Section } from '@/components/page/Section';
import { StatLine } from '@/components/page/StatLine';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { CLASSES, useClassName } from '@/features/dashboard/panels/allocationNames';
import { fieldErrorsOf, messageFor } from '../../subscriptions/fieldErrors';
import { PROJECTION_SCENARIO, SCENARIO, SCENARIOS, type ProjectionScenario } from '../api';
import type { ProjectionEvent } from './api';
import { comma } from './AssumptionFields';
import type { EventFieldError } from './EventDialog';
import { EventList } from './EventList';
import type { ScenarioDialog } from './ProjectionScenarioDialogs';
import { ProjectionWarnings } from './ProjectionWarnings';
import { bodyOf, useSave } from './save';
import '@/locales/scenarios';

// The chart (Recharts) loads with its tab; the rename and assumptions dialogs when one opens.
const ProjectionScenarioTab = lazy(() => import('./ProjectionScenarioTab'));
const ProjectionScenarioDialog = lazy(() => import('./ProjectionScenarioDialogs'));

const TABS = ['projection', 'events', 'assumptions'] as const;
const EVENT_PATH = /^events\[(\d+)\](?:\.(.+))?$/;

/** Events the reader changed and the server refused: shown, with each refusal under its event, until the next save. */
type Refused = { events: ProjectionEvent[]; errors: Record<number, EventFieldError[]> };

/**
 * Spec §8 `/scenarios/projection/:id`: the figures at the horizon inline, then « Projection » (base against scenario, milestones,
 * year ends), « Événements » and « Hypothèses ». Every change PUTs the whole document the reader was shown; every figure is the
 * server's, in the reading chosen (today's money by default).
 */
export function ProjectionScenarioPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { id } = useParams({ from: '/scenarios/projection/$id' });
  const answer = $api.useQuery('get', PROJECTION_SCENARIO, { params: { path: { id } } });
  return (
    <article className="space-y-6">
      {answer.error ? (
        <>
          <PageHeader crumbs={[{ label: t('scenarios.title'), to: '/scenarios' }]} title={t('scenarios.projection.detail.unavailable')} />
          <ProblemMessage problem={present(toProblem(answer.error))} />
        </>
      ) : answer.data ? <ScenarioView scenario={answer.data} />
        : <Skeleton role="status" aria-busy="true" aria-label={t('scenarios.projection.detail.loading')} className="h-40 w-full rounded-md" />}
    </article>
  );
}

function ScenarioView({ scenario }: { scenario: ProjectionScenario }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const readingId = useId();
  const [tab, setTab] = useDetailTab(TABS);
  const [real, setReal] = useState(true);
  const [deleting, setDeleting] = useState(false);
  const [dialog, setDialog] = useState<ScenarioDialog | null>(null);
  const [refused, setRefused] = useState<Refused | null>(null);
  const [problem, setProblem] = useState<Presentation | null>(null);
  // A saved dialog hands the focus back to the button that opened it, never <body>.
  const [returnTo, setReturnTo] = useState<ScenarioDialog | null>(null);
  const renameRef = useRef<HTMLButtonElement>(null);
  const assumptionsRef = useRef<HTMLButtonElement>(null);
  const moreRef = useRef<HTMLButtonElement>(null);
  useEffect(() => {
    if (!returnTo) return;
    (returnTo === 'rename' ? renameRef : assumptionsRef).current?.focus();
    setReturnTo(null);
  }, [returnTo]);

  const { result, canWrite } = scenario;
  const currency = result.base.currency;
  // The events last sent: shown while their save is pending, so a change made meanwhile builds on them (the list's actions wait too).
  const sent = useRef<{ events: ProjectionEvent[]; message: string }>({ events: [], message: '' });
  const saveEvents = useSave(scenario, () => { setRefused(null); setProblem(null); toast.success(sent.current.message); }, (error) => {
    const byEvent: Record<number, EventFieldError[]> = {};
    const fields = fieldErrorsOf(error);
    for (const f of fields) {
      const at = EVENT_PATH.exec(f.path);
      if (at) (byEvent[Number(at[1])] ??= []).push({ path: at[2] ?? '', message: messageFor(t, f) });
    }
    setRefused({ events: sent.current.events, errors: byEvent });
    setProblem(fields.length > 0 && fields.every((f) => EVENT_PATH.test(f.path)) ? null : present(toProblem(error)));
  });
  const pending = saveEvents.isPending ? saveEvents.variables?.body?.document?.events : undefined;
  const events = pending ?? refused?.events ?? result.document.events ?? [];
  const send = (next: ProjectionEvent[], change: 'added' | 'saved' | 'removed') => {
    if (saveEvents.isPending) return;
    sent.current = { events: next, message: t(`scenarios.projection.events.${change}`) };
    setProblem(null);
    saveEvents.mutate({ params: { path: { id: scenario.id } }, body: bodyOf(scenario, { document: { ...result.document, events: next } }) });
  };

  const remove = $api.useMutation('delete', SCENARIO, {
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['get', SCENARIOS] });
      void navigate({ to: '/scenarios' });
    },
  });

  const open = (which: ScenarioDialog) => setDialog(which);
  const saved = (message: string) => { setReturnTo(dialog); setDialog(null); toast.success(message); };

  const last = result.scenario.points.at(-1);
  // The last loan's end is the latest `loan_end` date the server gives (ISO dates compare as strings).
  const lastLoanEnd = result.scenario.milestones.filter((m) => m.kind === 'loan_end').map((m) => m.on).sort().at(-1);
  const money = (amount: string | undefined) => (amount === undefined ? t('scenarios.dash') : formatCurrency(amount, currency));
  const tabFallback = <Skeleton role="status" aria-busy="true" aria-label={t('scenarios.projection.detail.loading')} className="h-72 w-full rounded-md" />;

  return (
    <>
      <PageHeader crumbs={[{ label: t('scenarios.title'), to: '/scenarios' }]} title={scenario.name}
        meta={<AccessBadge access={scenario.visibility === 'private' ? 'private' : 'shared'} />}
        actions={(
          <>
            <Button asChild variant="outline">
              {/* A private scenario is visible in scope « Moi » only, whatever scope it was saved with. */}
              <Link to="/scenarios/compare"
                search={{ ids: scenario.id, ...(scenario.scope === 'me' || scenario.visibility === 'private' ? { scope: 'me' as const } : {}) }}>
                {t('scenarios.compare.open')}
              </Link>
            </Button>
            {canWrite ? (
              <>
                <Button ref={renameRef} type="button" variant="outline" onClick={() => open('rename')}><Pencil aria-hidden="true" />{t('scenarios.projection.detail.rename')}</Button>
                <RowMenu label={t('page.more_actions')} triggerRef={moreRef}
                  items={[{ label: t('scenarios.projection.detail.delete'), destructive: true, disabled: remove.isPending, onSelect: () => setDeleting(true) }]} />
              </>
            ) : null}
          </>
        )}>
        <section aria-label={t('scenarios.detail.figures')} className="space-y-2">
          <StatLine items={[
            { label: t(real ? 'scenarios.projection.detail.at_horizon_real' : 'scenarios.projection.detail.at_horizon_nominal'), value: money(last ? (real ? last.real : last.nominal) : undefined) },
            { label: t(real ? 'scenarios.projection.result.gap_real' : 'scenarios.projection.result.gap_nominal'), value: money(real ? result.gapReal : result.gapNominal) },
            { label: t('scenarios.projection.detail.last_loan_end'), value: lastLoanEnd ? <LocalDate value={lastLoanEnd} /> : t('scenarios.dash') },
          ]} />
          <ProjectionWarnings warnings={result.scenario.warnings} />
          <p className="text-sm text-muted-foreground">{t('scenarios.projection.result.reminder')}</p>
        </section>
      </PageHeader>
      {deleting ? (
        <ConfirmFromMenu label={t('scenarios.projection.detail.delete')} confirmation={t('scenarios.projection.detail.delete_confirm')} pending={remove.isPending}
          returnFocus={moreRef} onClose={() => setDeleting(false)} onConfirm={() => remove.mutate({ params: { path: { id: scenario.id } } })} />
      ) : null}
      {canWrite ? null : <p>{t('scenarios.projection.detail.read_only')}</p>}
      {remove.error ? <ProblemMessage problem={present(toProblem(remove.error))} /> : null}

      {canWrite && dialog ? (
        <Suspense fallback={null}>
          <ProjectionScenarioDialog which={dialog} scenario={scenario} onClose={() => setDialog(null)} onDone={saved} />
        </Suspense>
      ) : null}

      <DetailTabs value={tab} onChange={setTab} tabs={[
        {
          id: 'projection', label: t('scenarios.projection.detail.tab.projection'),
          action: (
            <>
              <span id={readingId} className="sr-only">{t('dashboard.projection.reading')}</span>
              <Segmented aria-labelledby={readingId} value={real ? 'real' : 'nominal'} onValueChange={(v) => setReal(v === 'real')}
                options={[{ value: 'real', label: t('dashboard.projection.real') }, { value: 'nominal', label: t('dashboard.projection.nominal') }]} />
            </>
          ),
          content: <Suspense fallback={tabFallback}><ProjectionScenarioTab comparison={result} real={real} /></Suspense>,
        },
        {
          id: 'events', label: t('scenarios.projection.detail.tab.events'),
          content: (
            <>
              {problem ? <ProblemMessage problem={problem} /> : null}
              <EventList events={events} errors={saveEvents.isPending ? undefined : refused?.errors} currency={currency}
                scope={scenario.scope === 'me' ? 'me' : 'household'} readOnly={!canWrite} busy={saveEvents.isPending} silent
                onAdd={(e) => send([...events, e], 'added')}
                onEdit={(i, e) => send(events.map((x, j) => (j === i ? e : x)), 'saved')}
                onRemove={(i) => send(events.filter((_, j) => j !== i), 'removed')} />
            </>
          ),
        },
        {
          id: 'assumptions', label: t('scenarios.projection.detail.tab.assumptions'),
          content: (
            <Section title={t('scenarios.projection.detail.assumptions')}>
              <Assumptions scenario={scenario} />
              {canWrite ? (
                <Button ref={assumptionsRef} type="button" variant="outline" onClick={() => open('assumptions')}>{t('scenarios.projection.detail.edit_assumptions')}</Button>
              ) : null}
            </Section>
          ),
        },
      ]} />
    </>
  );
}

/** The horizon and each assumption: the scenario's own as saved, else the household's (the base's), marked so. */
function Assumptions({ scenario }: { scenario: ProjectionScenario }) {
  const { t } = useTranslation();
  const className = useClassName();
  const { base, document } = scenario.result;
  const o = document.assumptions;
  const h = base.assumptions;
  const own = (value: string | null | undefined, household: string, show: (v: string) => string) =>
    (value == null ? t('scenarios.projection.detail.as_household', { value: show(household) }) : show(value));
  const rate = (v: string) => `${comma(v)} %`;
  const amount = (v: string) => formatCurrency(v, base.currency);
  const freed = (v: boolean) => t(v ? 'scenarios.projection.form.freed_yes' : 'scenarios.projection.form.freed_no');
  const horizon = document.horizon ?? scenario.result.scenario.horizon;
  const retirement = horizon.startsWith('retire:') ? base.retirementHorizons.find((r) => `retire:${r.person}` === horizon) : undefined;
  const row = (label: string, value: ReactNode) => (
    <div key={label} className="flex justify-between gap-3 border-b py-1 last:border-b-0">
      <dt className="text-muted-foreground">{label}</dt>
      <dd className="text-right tabular-nums">{value}</dd>
    </div>
  );
  return (
    <dl className="max-w-md text-sm">
      {row(t('scenarios.projection.detail.horizon'), retirement ? t('scenarios.projection.detail.horizon_retire', { name: retirement.name })
        : /^\d+y$/.test(horizon) ? t('scenarios.projection.form.horizon_years', { count: Number.parseInt(horizon, 10) }) : horizon)}
      {row(t('scenarios.projection.form.inflation'), own(o?.inflation, h.inflation, rate))}
      {row(t('scenarios.projection.form.monthly_savings'), own(o?.monthlySavings, base.monthlySavings, amount))}
      {row(t('scenarios.projection.form.savings_class'), own(o?.savingsClass, h.savingsClass, className))}
      {row(t('scenarios.projection.form.freed'), o?.freedPaymentsToSavings == null
        ? t('scenarios.projection.detail.as_household', { value: freed(h.freedPaymentsToSavings) }) : freed(o.freedPaymentsToSavings))}
      {CLASSES.map((c) => row(t('scenarios.projection.form.class_rate', { name: className(c) }),
        o?.classRates?.[c] == null && h.classRates[c] === undefined ? t('scenarios.dash') : own(o?.classRates?.[c], h.classRates[c] ?? '', rate)))}
    </dl>
  );
}

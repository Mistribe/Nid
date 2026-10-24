import { useParams } from '@tanstack/react-router';
import { Pencil } from 'lucide-react';
import { lazy, Suspense, useEffect, useRef, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { AccessBadge } from '@/components/AccessBadge';
import { Money } from '@/components/Money';
import { DetailTabs, useDetailTab } from '@/components/page/DetailTabs';
import { PageHeader } from '@/components/page/PageHeader';
import { Section } from '@/components/page/Section';
import { StatLine } from '@/components/page/StatLine';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { percent } from '@/features/dashboard/format';
import { CLASSES, useClassName } from '@/features/dashboard/panels/allocationNames';
import { AssumptionsDialog } from './AssumptionsDialog';
import { CapacityTab } from './CapacityTab';
import { SCENARIO, type Scenario } from './api';
import '@/locales/scenarios';

// The chart (Recharts) and the loans' schedules (their strings) load with their tab.
const ProjectionTab = lazy(() => import('./ProjectionTab'));
const LoansTab = lazy(() => import('./LoansTab'));

const TABS = ['capacity', 'projection', 'loans', 'assumptions'] as const;
type Editing = 'all' | 'assumptions';

/**
 * Spec §7 `/scenarios/:id`: the key figures inline under the name, then « Capacité », « Projection », « Prêts » and
 * « Hypothèses ». Every figure is the server's; an edit PUTs the whole document and the page shows the refetched result.
 */
export function ScenarioDetailPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { id } = useParams({ from: '/scenarios/$id' });
  const answer = $api.useQuery('get', SCENARIO, { params: { path: { id } } });
  const [tab, setTab] = useDetailTab(TABS);
  const [editing, setEditing] = useState<Editing | null>(null);
  const [announcement, setAnnouncement] = useState('');
  // A saved dialog hands the focus back to the page: it returns to the button that opened it, never <body>.
  const [returnTo, setReturnTo] = useState<Editing | null>(null);
  const editRef = useRef<HTMLButtonElement>(null);
  const assumptionsRef = useRef<HTMLButtonElement>(null);
  useEffect(() => {
    if (!returnTo) return;
    (returnTo === 'all' ? editRef : assumptionsRef).current?.focus();
    setReturnTo(null);
  }, [returnTo]);
  const open = (which: Editing) => { setAnnouncement(''); setEditing(which); };
  const saved = (message: string) => { setReturnTo(editing); setEditing(null); setAnnouncement(message); };

  if (answer.error) return <ProblemMessage problem={present(toProblem(answer.error))} />;
  if (!answer.data) return <div aria-busy="true" aria-label={t('scenarios.detail.loading')} className="h-40 animate-pulse rounded-md border bg-muted" />;

  const scenario = answer.data;
  const { capacity, gap } = scenario.result;
  const tabFallback = <div role="status" aria-busy="true" aria-label={t('scenarios.detail.loading')} className="h-40 animate-pulse rounded-md border bg-muted" />;

  return (
    <article className="space-y-6">
      <PageHeader crumbs={[{ label: t('scenarios.title'), to: '/scenarios' }]} title={scenario.name}
        meta={<><span className="text-sm text-muted-foreground">{t(`scenarios.use.${scenario.document.use === 'rental' ? 'rental' : 'primary'}`)}</span>
          <AccessBadge access={scenario.visibility === 'private' ? 'private' : 'shared'} /></>}
        actions={scenario.canWrite ? (
          <Button ref={editRef} type="button" variant="outline" onClick={() => open('all')}><Pencil aria-hidden="true" />{t('scenarios.detail.edit')}</Button>
        ) : null}>
        <section aria-label={t('scenarios.detail.figures')} className="space-y-2">
          <StatLine items={[
            { label: t('scenarios.summary.new_payment'), value: <Money value={capacity.newPayment} tabular /> },
            { label: t('scenarios.summary.debt_after'), value: capacity.debtRatioAfter == null ? t('scenarios.dash') : t('scenarios.percent', { value: percent(capacity.debtRatioAfter) }) },
            { label: t('scenarios.summary.left_after'), value: <Money value={capacity.leftAfter} tabular /> },
            { label: t('scenarios.detail.gap'), value: <Money value={gap} tabular /> },
          ]} />
          <p className="text-sm text-muted-foreground">{t('scenarios.summary.reminder')}</p>
        </section>
      </PageHeader>
      {scenario.canWrite ? null : <p>{t('scenarios.detail.read_only')}</p>}
      <p role="status" aria-live="polite">{announcement}</p>

      {scenario.canWrite && editing ? (
        <AssumptionsDialog scenario={scenario} onClose={() => setEditing(null)} onDone={saved}
          steps={editing === 'all' ? ['property', 'funding', 'loans', 'assumptions'] : ['assumptions']}
          title={editing === 'all' ? t('scenarios.detail.edit') : t('scenarios.detail.assumptions_title')} />
      ) : null}

      <DetailTabs value={tab} onChange={setTab} tabs={[
        { id: 'capacity', label: t('scenarios.detail.tab.capacity'), content: <CapacityTab result={scenario.result} /> },
        { id: 'projection', label: t('scenarios.detail.tab.projection'), content: <Suspense fallback={tabFallback}><ProjectionTab result={scenario.result} /></Suspense> },
        { id: 'loans', label: t('scenarios.detail.tab.loans'), content: <Suspense fallback={tabFallback}><LoansTab result={scenario.result} /></Suspense> },
        {
          id: 'assumptions', label: t('scenarios.detail.tab.assumptions'),
          content: (
            <Section title={t('scenarios.detail.assumptions')}>
              <Assumptions scenario={scenario} />
              {scenario.canWrite ? (
                <Button ref={assumptionsRef} type="button" variant="outline" onClick={() => open('assumptions')}>{t('scenarios.detail.edit_assumptions')}</Button>
              ) : null}
            </Section>
          ),
        },
      ]} />
    </article>
  );
}

/** The saved assumptions as typed: rates in percent (the server's strings, a French comma), the monthly savings in the scenario's currency. */
function Assumptions({ scenario }: { scenario: Scenario }) {
  const { t } = useTranslation();
  const className = useClassName();
  const a = scenario.document.assumptions;
  const rate = (value: string | null | undefined) => (value == null ? t('scenarios.dash') : value.replace('.', ','));
  const currency = scenario.result.capacity.newPayment.currency;
  const row = (label: string, value: ReactNode) => (
    <div key={label} className="flex justify-between gap-3 border-b py-1 last:border-b-0">
      <dt className="text-muted-foreground">{label}</dt>
      <dd className="text-right tabular-nums">{value}</dd>
    </div>
  );
  return (
    <dl className="max-w-md text-sm">
      {row(t('scenarios.form.horizon'), a.horizonYears)}
      {row(t('scenarios.form.monthly_savings'), <Money value={{ amount: a.monthlySavings, currency }} tabular />)}
      {row(t('scenarios.form.savings_class'), className(a.savingsClass))}
      {CLASSES.map((c) => row(t('scenarios.form.class_rate', { name: className(c) }), rate(a.classRates[c])))}
      {row(t('scenarios.form.property_growth'), rate(a.propertyGrowth))}
    </dl>
  );
}

import { Link, useParams } from '@tanstack/react-router';
import { AlertTriangle, Archive } from 'lucide-react';
import { lazy, Suspense, useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { AccessBadge } from '@/components/AccessBadge';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Section } from '../income/Section';
import { todayIso } from '../subscriptions/today';
import { LOAN, SCHEDULE } from './api';
import { FeesAndRepayments, LoanContract } from './detail/LoanContract';
import { LoanFigures } from './detail/LoanFigures';
import { ScheduleTable } from './detail/ScheduleTable';
import { money, warningText } from './format';
import { SettingsForm } from './settings/SettingsForm';

// Loaded on demand, as the dashboard's charts: Recharts stays out of the detail's own chunk.
const ScheduleChart = lazy(() => import('./chart/ScheduleChart').then((m) => ({ default: m.ScheduleChart })));

/** 3B1 design §7: a loan read — its figures today, warnings, contract, fees and repayments, the schedule by year, settings. */
export function LoanDetailPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { id } = useParams({ from: '/loans/$id' });
  const answer = $api.useQuery('get', LOAN, { params: { path: { id } } });
  const [settingsAnnouncement, setSettingsAnnouncement] = useState('');
  const back = <Link to="/loans" className="text-sm underline">{t('loan_detail.back')}</Link>;

  // An unknown loan and another adult's private one both answer 404: « Cet élément est introuvable. ».
  if (answer.error) return <>{back}<ProblemMessage problem={present(toProblem(answer.error))} /></>;
  if (!answer.data) return <>{back}<div aria-busy="true" aria-label={t('loan_detail.loading')} className="h-40 animate-pulse rounded-md border bg-muted" /></>;

  const loan = answer.data;
  return (
    <article className="space-y-6">
      {back}
      <header className="flex flex-wrap items-center gap-3">
        <h1 className="text-2xl font-semibold">{loan.name}</h1>
        {loan.lender ? <span className="text-sm text-muted-foreground">{loan.lender}</span> : null}
        <AccessBadge access={loan.visibility === 'private' ? 'private' : 'shared'} />
        {loan.ended ? (
          <span className="inline-flex items-center gap-1 text-sm"><Archive aria-hidden="true" className="size-4" />{t('loan_detail.ended')}</span>
        ) : null}
      </header>
      {loan.canWrite ? null : <p>{t('loan_detail.read_only')}</p>}
      {loan.warnings.length > 0 ? (
        <ul aria-label={t('loan_detail.warnings')} className="space-y-1">
          {loan.warnings.map((w, i) => (
            <li key={`${w.code}-${w.row}-${i}`} className="flex items-center gap-2">
              <AlertTriangle aria-hidden="true" className="size-4 text-status-warning" />
              {warningText(t, w)}
            </li>
          ))}
        </ul>
      ) : null}
      <Section title={t('loan_detail.figures')}><LoanFigures loan={loan} /></Section>
      <Section title={t('loan_detail.contract')}><LoanContract loan={loan} /></Section>
      {loan.fees.length > 0 || loan.repayments.length > 0 ? (
        <Section title={t('loan_detail.fees_and_repayments')}><FeesAndRepayments loan={loan} /></Section>
      ) : null}
      <Section title={t('loan_detail.schedule.title')}><Schedule id={loan.id} currency={loan.currency} /></Section>
      {loan.canWrite ? (
        <Section title={t('loan_detail.settings.title')} announcement={settingsAnnouncement}>
          <SettingsForm loan={loan} onStart={() => setSettingsAnnouncement('')} onDone={setSettingsAnnouncement} />
        </Section>
      ) : null}
    </article>
  );
}

/** The schedule's own request: the rest of the page reads without waiting for its rows. */
function Schedule({ id, currency }: { id: string; currency: string }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const summaryId = useId();
  const answer = $api.useQuery('get', SCHEDULE, { params: { path: { id } } });
  if (answer.error) return <ProblemMessage problem={present(toProblem(answer.error))} />;
  if (!answer.data) return <div role="status" aria-busy="true" aria-label={t('loan_detail.schedule.loading')} className="h-40 animate-pulse rounded-md border bg-muted" />;

  const plan = answer.data;
  const first = plan.years[0];
  const last = plan.years[plan.years.length - 1];
  return (
    <div className="space-y-4">
      {first && last ? (
        <>
          {/* Spec §7: the chart's reading for screen readers, outside the lazy chunk so it never waits for it. */}
          <p id={summaryId} className="sr-only">
            {t('loan_detail.chart.summary', {
              first: first.year, last: last.year, interest: money(currency, plan.totalInterest), principal: money(currency, plan.totalPrincipal),
            })}
          </p>
          <Suspense fallback={<div className="h-64 animate-pulse rounded-md border bg-muted" />}>
            <ScheduleChart years={plan.years} currency={currency} summaryId={summaryId} />
          </Suspense>
        </>
      ) : null}
      <ScheduleTable schedule={plan} currency={currency} today={todayIso()} />
    </div>
  );
}

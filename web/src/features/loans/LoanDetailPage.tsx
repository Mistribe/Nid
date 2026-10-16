import { Link, useLocation, useParams } from '@tanstack/react-router';
import { AlertTriangle, Archive } from 'lucide-react';
import { lazy, Suspense, useEffect, useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { AccessBadge } from '@/components/AccessBadge';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Section } from '../income/Section';
import { LOAN, SCHEDULE } from './api';
import { LoanContract } from './detail/LoanContract';
import { LoanFigures } from './detail/LoanFigures';
import { ScheduleTable } from './detail/ScheduleTable';
import { FeesSection } from './fees/FeesSection';
import { money, warningText } from './format';
import type { RepaymentPrefill } from './repayments/repayment';
import { RepaymentsSection } from './repayments/RepaymentsSection';
import { SettingsForm } from './settings/SettingsForm';
import { SimulationSection } from './simulation/SimulationSection';

// Loaded on demand, as the dashboard's charts: Recharts stays out of the detail's own chunk.
const ScheduleChart = lazy(() => import('./chart/ScheduleChart').then((m) => ({ default: m.ScheduleChart })));
// 3F2: the bank's table, its own chunk too: the paste form and its preview load with the section only.
const BankScheduleSection = lazy(() => import('./bank/BankScheduleSection').then((m) => ({ default: m.BankScheduleSection })));

/** 3B1 design §7, 3B2: a loan — its figures today, warnings, contract, fees and repayments, the schedule by year, settings. */
export function LoanDetailPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { id } = useParams({ from: '/loans/$id' });
  const saved = useLocation({ select: (l) => l.state.loanSaved });
  const answer = $api.useQuery('get', LOAN, { params: { path: { id } } });
  const [settingsAnnouncement, setSettingsAnnouncement] = useState('');
  // The page's own live region (3B2 design §5): the contract page lands here with « Contrat enregistré », written after
  // mount so assistive technologies hear it. ponytail: the history entry keeps the state, so reloading it says it again.
  const [announcement, setAnnouncement] = useState('');
  useEffect(() => { if (saved === 'contract') setAnnouncement(t('loan_contract.saved')); }, [saved, t]);
  // 3B2 design §8: the simulation hands a column to the repayments' add form; `seq` makes each press a new one.
  const [prefill, setPrefill] = useState<RepaymentPrefill | null>(null);
  const record = (p: Omit<RepaymentPrefill, 'seq'>) => setPrefill((prev) => ({ ...p, seq: (prev?.seq ?? 0) + 1 }));
  const loan = answer.data;

  return (
    <article className="space-y-6">
      <Link to="/loans" className="text-sm underline">{t('loan_detail.back')}</Link>
      <p role="status" aria-live="polite">{announcement}</p>
      {answer.error ? (
        // An unknown loan and another adult's private one both answer 404: « Cet élément est introuvable. ».
        <ProblemMessage problem={present(toProblem(answer.error))} />
      ) : !loan ? (
        <div aria-busy="true" aria-label={t('loan_detail.loading')} className="h-40 animate-pulse rounded-md border bg-muted" />
      ) : (
        <>
          <header className="flex flex-wrap items-center gap-3">
            <h1 className="text-2xl font-semibold">{loan.name}</h1>
            {loan.lender ? <span className="text-sm text-muted-foreground">{loan.lender}</span> : null}
            <AccessBadge access={loan.visibility === 'private' ? 'private' : 'shared'} />
            {loan.ended ? (
              <span className="inline-flex items-center gap-1 text-sm"><Archive aria-hidden="true" className="size-4" />{t('loan_detail.ended')}</span>
            ) : null}
          </header>
          {loan.canWrite ? null : <p>{t('loan_detail.read_only')}</p>}
          {loan.propertyId ? (
            <p>
              {t('loan_detail.property')}{' '}
              {loan.propertyName
                ? <Link to="/properties/$id" params={{ id: loan.propertyId }} className="underline">{loan.propertyName}</Link>
                : t('loan_detail.property_unreadable')}
            </p>
          ) : null}
          {loan.warnings.length > 0 ? (
            <ul aria-label={t('loan_detail.warnings')} className="space-y-1">
              {loan.warnings.map((w, i) => (
                <li key={`${w.code}-${w.row}-${i}`} className="flex items-center gap-2">
                  <AlertTriangle aria-hidden="true" className="size-4 text-status-warning" />
                  {warningText(t, w, loan)}
                </li>
              ))}
            </ul>
          ) : null}
          <Section title={t('loan_detail.figures')} guide="loans.figures"><LoanFigures loan={loan} /></Section>
          {loan.ended ? null : <SimulationSection loan={loan} onRecord={loan.canWrite ? record : undefined} />}
          <Section title={t('loan_detail.contract')}>
            {loan.canWrite ? <Link to="/loans/$id/contract" params={{ id: loan.id }} className="text-sm underline">{t('loan_detail.edit_contract')}</Link> : null}
            <LoanContract loan={loan} />
          </Section>
          <FeesSection loan={loan} />
          <RepaymentsSection loan={loan} prefill={prefill} />
          <Suspense fallback={<div role="status" aria-busy="true" aria-label={t('loan_bank.loading')} className="h-24 animate-pulse rounded-md border bg-muted" />}>
            <BankScheduleSection loan={loan} />
          </Suspense>
          <Section title={t('loan_detail.schedule.title')} guide="loans.schedule"><Schedule id={loan.id} currency={loan.currency} nextPaymentOn={loan.figures.nextPaymentOn} /></Section>
          {loan.canWrite ? (
            <Section title={t('loan_detail.settings.title')} announcement={settingsAnnouncement}>
              <SettingsForm loan={loan} onStart={() => setSettingsAnnouncement('')} onDone={setSettingsAnnouncement} />
            </Section>
          ) : null}
        </>
      )}
    </article>
  );
}

/** The schedule's own request: the rest of the page reads without waiting for its rows. */
function Schedule({ id, currency, nextPaymentOn }: { id: string; currency: string; nextPaymentOn?: string | null }) {
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
      <ScheduleTable schedule={plan} currency={currency} nextPaymentOn={nextPaymentOn} />
    </div>
  );
}

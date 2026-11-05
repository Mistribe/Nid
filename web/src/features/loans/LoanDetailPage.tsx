import { Link, useLocation, useParams } from '@tanstack/react-router';
import { AlertTriangle, Archive } from 'lucide-react';
import { lazy, Suspense, useEffect, useId, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { AccessBadge } from '@/components/AccessBadge';
import { LocalDate } from '@/components/LocalDate';
import { DetailTabs, useDetailTab } from '@/components/page/DetailTabs';
import { PageHeader } from '@/components/page/PageHeader';
import { PageTrail } from '@/components/page/trail';
import { SharingBadges } from '@/components/ownership/SharingBadges';
import { SharingMenu } from '@/components/ownership/SharingMenu';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { Section } from '@/components/page/Section';
import { LOAN, SCHEDULE, type LoanDetail } from './api';
import { LoanContract } from './detail/LoanContract';
import { LoanCost, LoanProgress, LoanStats } from './detail/LoanFigures';
import { ScheduleTable } from './detail/ScheduleTable';
import { FeesSection } from './fees/FeesSection';
import { money, warningText } from './format';
import type { RepaymentPrefill } from './repayments/repayment';
import { RepaymentsSection } from './repayments/RepaymentsSection';
import { ScheduleYearsTable } from './chart/ScheduleYearsTable';
import { SettingsForm } from './settings/SettingsForm';
import { TableToggle } from '@/components/page/TableToggle';
import { SimulationSection } from './simulation/SimulationSection';
import '@/locales/loans';
import { ItemDocuments } from '@/features/documents/ItemDocuments';
import '@/locales/documents';

// Loaded on demand, as the dashboard's charts: Recharts stays out of the detail's own chunk.
const ScheduleChart = lazy(() => import('./chart/ScheduleChart').then((m) => ({ default: m.ScheduleChart })));
// 3F2: the bank's table, its own chunk too: the paste form and its preview load with the section only.
const BankScheduleSection = lazy(() => import('./bank/BankScheduleSection').then((m) => ({ default: m.BankScheduleSection })));

const TABS = ['overview', 'schedule', 'operations', 'contract', 'documents'] as const;

/**
 * 3B1 design §7, 3B2, redesign §5.2–5.3: a loan as a tabbed page. Under the title, its figures today; the overview adds
 * the repaid part of the principal, warnings, the next payments and the yearly chart; the schedule holds the table by
 * year and the bank's own; operations the fees, the early repayments and the simulation; the contract its terms, its
 * edition and the settings. The simulation is the header's action: it opens the operations and lands on its heading.
 */
export function LoanDetailPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { id } = useParams({ from: '/loans/$id' });
  const saved = useLocation({ select: (l) => l.state.loanSaved });
  const answer = $api.useQuery('get', LOAN, { params: { path: { id } } });
  const [tab, setTab] = useDetailTab(TABS);
  // The contract page lands here with « Contrat enregistré » (3B2 design §5); the id keeps StrictMode's second run to one
  // toast. ponytail: the history entry keeps the state, so reloading it says it again.
  useEffect(() => { if (saved === 'contract') toast.success(t('loan_contract.saved'), { id: 'loan-contract-saved' }); }, [saved, t]);
  // 3B2 design §8: the simulation hands a column to the repayments' add dialog; `seq` makes each press a new one.
  const [prefill, setPrefill] = useState<RepaymentPrefill | null>(null);
  const record = (p: Omit<RepaymentPrefill, 'seq'>) => setPrefill((prev) => ({ ...p, seq: (prev?.seq ?? 0) + 1 }));
  // The header's « Simuler » switches to the operations; the simulation, once there, takes the focus (`landing`).
  const [landOnSimulation, setLandOnSimulation] = useState(false);
  const loan = answer.data;
  // Loans dashboard §5.3: the household's simulation hands this loan its part; opening « Opérations » replaces the history
  // entry without that state, so it is consumed once.
  const handed = useLocation({ select: (l) => l.state.loanRepayment });
  const takenRef = useRef(false);
  useEffect(() => {
    if (!handed || !loan || takenRef.current) return;
    takenRef.current = true;
    if (!loan.canWrite) return;
    record(handed);
    setTab('operations');
  }, [handed, loan]); // eslint-disable-line react-hooks/exhaustive-deps -- once the loan is read; `record` and `setTab` are recreated every render
  // No `PageHeader` yet: the bar's trail leads back to the list (no inline link of its own).
  const crumbs = [{ label: t('nav.loans'), to: '/loans' as const }];
  const trail = <PageTrail crumbs={crumbs} />;

  if (answer.error) {
    // An unknown loan and another adult's private one both answer 404: « Cet élément est introuvable. ».
    return <article className="space-y-6">{trail}<ProblemMessage problem={present(toProblem(answer.error))} /></article>;
  }
  if (!loan) return <article className="space-y-6">{trail}<div aria-busy="true" aria-label={t('loan_detail.loading')} className="h-40 shimmer rounded-md border bg-muted" /></article>;

  const actions = (
    <>
      {loan.ended ? null : (
        <Button type="button" data-guide="loans.simulation" onClick={() => { setLandOnSimulation(true); setTab('operations'); }}>{t('loan_detail.simulate')}</Button>
      )}
      <SharingMenu item={id} onAnnounce={toast.success} shared={loan.canWrite && loan.visibility === 'shared'} />
    </>
  );

  return (
    <article className="space-y-6">
      <PageHeader crumbs={crumbs} title={loan.name} actions={actions}
        meta={(
          <>
            {loan.lender ? <span className="text-sm text-muted-foreground">{loan.lender}</span> : null}
            <AccessBadge access={loan.visibility === 'private' ? 'private' : 'shared'} /><SharingBadges item={id} />
            {loan.ended ? (
              <span className="inline-flex items-center gap-1 text-sm"><Archive aria-hidden="true" className="size-4" />{t('loan_detail.ended')}</span>
            ) : null}
          </>
        )}>
        <section aria-label={t('loan_detail.figures')} data-guide="loans.figures" className="space-y-2">
          <LoanStats loan={loan} />
        </section>
      </PageHeader>
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
      <DetailTabs value={tab} onChange={setTab} tabs={[
        { id: 'overview', label: t('loan_detail.tab.overview'), content: <Overview loan={loan} /> },
        { id: 'schedule', label: t('loan_detail.tab.schedule'), guide: 'loans.schedule', content: <ScheduleTab loan={loan} /> },
        {
          id: 'operations', label: t('loan_detail.tab.operations'),
          content: (
            <>
              {loan.ended ? null : <SimulationSection loan={loan} onRecord={loan.canWrite ? record : undefined} landing={landOnSimulation} onLanded={() => setLandOnSimulation(false)} />}
              <FeesSection loan={loan} />
              <RepaymentsSection loan={loan} prefill={prefill} onConsumed={() => setPrefill(null)} />
            </>
          ),
        },
        {
          id: 'contract', label: t('loan_detail.tab.contract'),
          content: (
            <>
              <Section title={t('loan_detail.contract')}>
                {loan.canWrite ? <Link to="/loans/$id/contract" params={{ id: loan.id }} className="text-sm underline">{t('loan_detail.edit_contract')}</Link> : null}
                <LoanContract loan={loan} />
              </Section>
              {loan.canWrite ? (
                <Section title={t('loan_detail.settings.title')}>
                  <SettingsForm loan={loan} onDone={toast.success} />
                </Section>
              ) : null}
            </>
          ),
        },
        { id: 'documents' as const, label: t('documents_item.title'), content: <ItemDocuments itemId={loan.id} canWrite={loan.canWrite} defaultRole="loan_offer" /> },
      ]} />
    </article>
  );
}

const loadingRow = (label: string, height = 'h-40') => <div role="status" aria-busy="true" aria-label={label} className={`${height} shimmer rounded-md border bg-muted`} />;

/** Where the loan stands: the repaid part of the principal, the next payments, the yearly chart, the cost. */
function Overview({ loan }: { loan: LoanDetail }) {
  const { t } = useTranslation();
  return (
    <>
      <Section title={t('loan_detail.principal_repaid')}><LoanProgress loan={loan} /></Section>
      <OverviewSchedule loan={loan} />
      <Section title={t('loan_detail.cost')}><LoanCost loan={loan} /></Section>
    </>
  );
}

/** The schedule's own request: the rest of the page reads without waiting for its rows. */
function OverviewSchedule({ loan }: { loan: LoanDetail }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const summaryId = useId();
  const [asTable, setAsTable] = useState(false);
  const answer = $api.useQuery('get', SCHEDULE, { params: { path: { id: loan.id } } });
  if (answer.error) return <ProblemMessage problem={present(toProblem(answer.error))} />;
  if (!answer.data) return loadingRow(t('loan_detail.schedule.loading'));

  const plan = answer.data;
  const first = plan.years[0];
  const last = plan.years[plan.years.length - 1];
  const from = loan.figures.nextPaymentOn ? plan.rows.findIndex((r) => r.dueDate === loan.figures.nextPaymentOn) : -1;
  const upcoming = from >= 0 ? plan.rows.slice(from, from + 3) : [];
  const m = (amount: string) => money(loan.currency, amount);
  return (
    <>
      {upcoming.length > 0 ? (
        <Section title={t('loan_detail.upcoming')}>
          <Table aria-label={t('loan_detail.upcoming')}>
            <TableHeader>
              <TableRow>
                <TableHead>{t('loan_detail.schedule.col_number')}</TableHead>
                <TableHead>{t('loan_detail.schedule.col_date')}</TableHead>
                <TableHead className="text-right">{t('loan_detail.schedule.col_payment')}</TableHead>
                <TableHead className="text-right">{t('loan_detail.schedule.col_interest')}</TableHead>
                <TableHead className="text-right">{t('loan_detail.schedule.col_principal')}</TableHead>
                <TableHead className="text-right">{t('loan_detail.schedule.col_balance')}</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {upcoming.map((r) => (
                <TableRow key={r.number}>
                  <TableCell className="tabular-nums">{r.number}</TableCell>
                  <TableCell><LocalDate value={r.dueDate} /></TableCell>
                  <TableCell className="text-right tabular-nums">{m(r.payment)}</TableCell>
                  <TableCell className="text-right tabular-nums">{m(r.interest)}</TableCell>
                  <TableCell className="text-right tabular-nums">{m(r.principal)}</TableCell>
                  <TableCell className="text-right tabular-nums">{m(r.balance)}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </Section>
      ) : null}
      {first && last ? (
        <Section title={t('loan_detail.chart.title')} actions={<TableToggle asTable={asTable} onToggle={() => setAsTable((v) => !v)} />}>
          {/* Spec §7: the chart's reading for screen readers, outside the lazy chunk so it never waits for it. */}
          <p id={summaryId} className="sr-only">
            {t('loan_detail.chart.summary', {
              first: first.year, last: last.year, interest: m(plan.totalInterest), principal: m(plan.totalPrincipal),
            })}
          </p>
          {asTable ? <ScheduleYearsTable years={plan.years} currency={loan.currency} /> : (
            <Suspense fallback={<div className="h-64 shimmer rounded-md border bg-muted" />}>
              <ScheduleChart years={plan.years} currency={loan.currency} summaryId={summaryId} />
            </Suspense>
          )}
        </Section>
      ) : null}
    </>
  );
}

/** The schedule by year with today's payment marked, then the bank's table and its comparison. */
function ScheduleTab({ loan }: { loan: LoanDetail }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const answer = $api.useQuery('get', SCHEDULE, { params: { path: { id: loan.id } } });
  return (
    <>
      <Section title={t('loan_detail.schedule.title')}>
        {answer.error ? <ProblemMessage problem={present(toProblem(answer.error))} />
          : !answer.data ? loadingRow(t('loan_detail.schedule.loading'))
            : <ScheduleTable schedule={answer.data} currency={loan.currency} nextPaymentOn={loan.figures.nextPaymentOn} />}
      </Section>
      <Suspense fallback={loadingRow(t('loan_bank.loading'), 'h-24')}>
        <BankScheduleSection loan={loan} />
      </Suspense>
    </>
  );
}

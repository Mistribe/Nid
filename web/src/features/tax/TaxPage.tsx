import { keepPreviousData } from '@tanstack/react-query';
import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import { Fragment, type ReactNode } from 'react';
import { AlertTriangle } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import type { TaxSearch } from '@/app/router';
import { Segmented } from '@/components/form/Segmented';
import { SelectField } from '@/components/form/SelectField';
import { DetailTabs, useDetailTab } from '@/components/page/DetailTabs';
import { FilterField, FilterRow } from '@/components/page/FilterRow';
import { PageHeader } from '@/components/page/PageHeader';
import { StatLine } from '@/components/page/StatLine';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';
import { toPlot } from '../investments/chart/plot';
import { Section } from '@/components/page/Section';
import { PERSONS } from '../subscriptions/api';
import { todayIso } from '../subscriptions/today';
import { FIRST_ESTIMATE_YEAR, TAX, type TaxEstimate } from './api';
import { useTaxAssessments, type TaxAssessment } from './assessments/api';
import { GapTable } from './assessments/GapTable';
import { AssessmentView } from './assessments/AssessmentView';
import { noticeAmount, noticeText, percentText, PER_WARNINGS, warningText, ZERO } from './format';
import { PerRows, PerSummary, perWarningsInBlock } from './PerSection';
import { ProfileForm } from './profile/ProfileForm';
import { ProfileSubmit } from './profile/ProfileSubmit';
import { useProfileDraft } from './profile/useProfileDraft';
import { DeclarantTable, eur, ResultTable } from './ResultTable';
import { EvolutionSection } from './EvolutionSection';
import { SaleTotalSection } from './SaleTotalSection';
import { SimulationForm } from './SimulationForm';
import { Rental2044Section } from './Rental2044Section';
import { WithholdingSection } from './WithholdingSection';
import '@/locales/tax';

const TABS = ['detail', 'withholding', 'assessment', 'evolution', 'simulation', 'profile', '2044'] as const;

/** Past this many years the selector is a list, not a row of buttons. */
const MAX_SEGMENTS = 6;

/**
 * Tax estimate spec §9: the year's figures inline, then the calculation step by step, the household's profile, and a
 * simulation of edited inputs. Every figure is the server's; the page only formats them.
 */
export function TaxPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const search: TaxSearch = useSearch({ strict: false });
  const navigate = useNavigate();
  const scope = search.scope === 'me' ? 'me' : 'household';
  const thisYear = todayIso().slice(0, 4);
  const lastYear = String(Number(thisYear) - 1);
  const [rawTab, setTab] = useDetailTab(TABS);
  // The 2044 is filed for the year just ended; the estimate opens on the current one. The default stays out of the URL.
  const defaultYear = rawTab === '2044' ? lastYear : thisYear;
  const list = useTaxAssessments(scope);
  const assessments = list.data?.items ?? [];
  const years = [...new Set([lastYear, thisYear, ...assessments.map((a) => String(a.year))])].sort().map((y) => ({ value: y, label: y }));
  // A year the page does not offer (the server refuses it) falls back to the tab's default.
  // Until the notices are known, a past year in the URL is kept (and not estimated: the server would refuse it).
  const awaiting = list.isPending && Number(search.year) < FIRST_ESTIMATE_YEAR;
  const year = awaiting || years.some((y) => y.value === search.year) ? search.year! : defaultYear;
  const setYear = (next: string) => {
    void navigate({ to: '.', replace: true, resetScroll: false, search: ((prev: Record<string, unknown>) => ({ ...prev, year: next === defaultYear ? undefined : next })) as never });
  };
  const past = Number(year) < FIRST_ESTIMATE_YEAR;
  // The year changes with the tab (the 2044's is last year): the page stays on the previous estimate while the next loads, so the
  // menu does not vanish under the pointer.
  const answer = $api.useQuery('get', TAX, { params: { query: { year: Number(year), scope } } }, { enabled: !past, placeholderData: keepPreviousData });
  const estimate = past ? undefined : answer.data;
  const people = $api.useQuery('get', PERSONS).data ?? [];
  const notices = assessments.filter((a) => String(a.year) === year);
  // The Avis tab exists only for a year with notices: a URL asking for it elsewhere shows Calcul.
  const tab = rawTab === 'assessment' && !notices.length ? 'detail' : rawTab;
  const noticeViews = <Notices notices={notices} persons={people} gaps={estimate?.gaps} adults={estimate?.adults ?? []} />;
  // Held here, not in the tab: Radix unmounts a hidden tab, and the draft outlives a tab switch.
  const draft = useProfileDraft(estimate, scope);

  return (
    <div className="space-y-6" aria-busy={answer.isPlaceholderData || undefined}>
      <PageHeader title={t('tax.title')} actions={<Button asChild className="print:hidden"><Link to="/tax/assessments/new">{t('tax.add_assessment')}</Link></Button>} />
      <div className="print:hidden">
        <FilterRow label={t('common.filters')}>
          <FilterField label={t('tax.year')}>
            {(labelId, controlId) => years.length > MAX_SEGMENTS
              ? <div className="w-28"><SelectField id={controlId} aria-labelledby={labelId} value={year} onValueChange={setYear} options={years} /></div>
              : <Segmented aria-labelledby={labelId} value={year} onValueChange={setYear} options={years} />}
          </FilterField>
        </FilterRow>
      </div>
      {estimate ? <div className={tab === '2044' ? 'print:hidden' : undefined}><Figures estimate={estimate} onAdjust={() => setTab('withholding')} /></div> : null}
      {past && awaiting ? <div aria-busy="true" aria-label={t('tax.loading')} className="h-40 shimmer rounded-md border bg-muted" /> : null}
      {past && !awaiting ? noticeViews : null}
      {!past && answer.error ? <ProblemMessage problem={present(toProblem(answer.error))} /> : null}
      {!past && !estimate && !answer.error ? <div aria-busy="true" aria-label={t('tax.loading')} className="h-40 shimmer rounded-md border bg-muted" /> : null}
      {estimate ? (
        <DetailTabs value={tab} onChange={setTab} tabs={[
          { id: 'detail', label: t('tax.tab.detail'), content: (
            <>
              <MoreFigures result={estimate.result} />
              <Split result={estimate.result} />
              <Notes estimate={estimate} />
              <Detail estimate={estimate} onProfile={() => setTab('profile')} />
            </>
          ) },
          { id: 'withholding', label: t('tax.tab.withholding'), content: <WithholdingSection estimate={estimate} /> },
          ...(notices.length ? [{ id: 'assessment' as const, label: t('tax.tab.assessment'), content: noticeViews }] : []),
          { id: 'evolution', label: t('tax.tab.evolution'),
            content: <EvolutionSection years={years.map((y) => y.value)} assessments={assessments} scope={scope} current={year} adults={estimate.adults} /> },
          { id: 'simulation', label: t('tax.tab.simulation'), content: (
            <>
              <SimulationForm key={estimate.year} estimate={estimate} />
              <Section title={t('tax.tab.sale')}><SaleTotalSection scope={scope} /></Section>
            </>
          ) },
          { id: 'profile', label: t('tax.tab.profile'), content: draft ? <ProfileForm key={draft.key} estimate={estimate} draft={draft} /> : null,
            action: draft ? <ProfileSubmit draft={draft} /> : undefined },
          { id: '2044', label: t('tax.tab.2044'), content: (
            <>
              {/* The tab list does not print: the printed 2044 keeps its title. */}
              <h2 className="hidden text-xl font-semibold print:block">{t('tax.d2044.print_title', { year })}</h2>
              <Rental2044Section year={Number(year)} scope={scope} />
            </>
          ) },
        ]} />
      ) : null}
    </div>
  );
}

function Notices({ notices, persons, gaps, adults }: {
  notices: TaxAssessment[]; persons: React.ComponentProps<typeof AssessmentView>['persons']; gaps: TaxEstimate['gaps']; adults: TaxEstimate['adults'];
}) {
  return <>{notices.map((a) => (
    <Fragment key={a.id}>
      {gaps && gaps.assessment === a.id ? <GapTable gaps={gaps} adults={adults} /> : null}
      <AssessmentView assessment={a} persons={persons} />
    </Fragment>
  ))}</>;
}

/** The previous year's scale in use: said under the total, where the scale is named every year, rather than among the notes. */
const CARRIED = 'rules_carried_forward';

/** The three figures that answer « combien, combien il reste, que faire en septembre ». */
function Figures({ estimate, onAdjust }: { estimate: TaxEstimate; onAdjust: () => void }) {
  const { t } = useTranslation();
  const r = estimate.result;
  const balance = r.withholding.balance;
  const refund = balance.startsWith('-');
  return (
    <StatLine items={[
      { label: t('tax.figure.total_tax'), value: (
        <>
          {eur(r.totalTax)}
          {/* Both lines show every year, so changing the year does not move the screen. */}
          <Sub className="min-h-5">{r.averageRate ? t('tax.figure.average_rate', { rate: percentText(t, r.averageRate) }) : null}</Sub>
          <Sub>{t(r.warnings.includes(CARRIED) ? 'tax.figure.carried' : 'tax.figure.scale', { year: r.rulesYear })}</Sub>
        </>
      ) },
      { label: t('tax.figure.left_monthly'), value: <>{eur(r.netAfterTaxMonthly)}<Sub>{t('tax.figure.per_year', { amount: eur(r.netAfterTax) })}</Sub></> },
      { label: t('tax.figure.balance', { year: estimate.year + 1 }), value: (
        <>
          {ZERO.test(balance) ? t('tax.figure.no_balance') : (
            <>{eur(refund ? balance.slice(1) : balance)} <span className="text-base font-medium">{t(refund ? 'tax.figure.refund' : 'tax.figure.to_pay')}</span></>
          )}
          <Sub><Button variant="link" className="h-auto p-0" onClick={onAdjust}>{t('tax.figure.adjust')}</Button></Sub>
        </>
      ) },
    ]} />
  );
}

function Sub({ className, children }: { className?: string; children: ReactNode }) {
  return <span className={cn('block text-sm font-normal text-muted-foreground', className)}>{children}</span>;
}

/** The rest of the year's figures, at the top of « Calcul ». */
function MoreFigures({ result: r }: { result: TaxEstimate['result'] }) {
  const { t } = useTranslation();
  return (
    <dl className="flex flex-wrap gap-x-6 gap-y-1 text-sm">
      {[
        [t('tax.figure.net_taxable'), eur(r.netTaxableIncome)],
        [t('tax.figure.marginal_rate'), percentText(t, r.marginalRate)],
        [t('tax.figure.withholding_rate'), percentText(t, r.withholding.rate)],
        [t('tax.figure.parts'), r.parts],
      ].map(([label, value]) => (
        <div key={label} className="flex gap-1.5"><dt className="text-muted-foreground">{label}</dt><dd className="font-medium tabular-nums">{value}</dd></div>
      ))}
    </dl>
  );
}

/** Where the net income goes: the bar is sized by plotting numbers (`toPlot`), every figure read is the server's string. */
function Split({ result: r }: { result: TaxEstimate['result'] }) {
  const { t } = useTranslation();
  const parts = [
    { label: t('tax.split.left'), amount: r.netAfterTax, colour: 'bg-muted-foreground/30' },
    { label: t('tax.row.income_tax'), amount: r.incomeTax, colour: 'bg-chart-1' },
    { label: t('tax.row.social_levies'), amount: r.socialLevies, colour: 'bg-chart-2' },
    { label: t('tax.row.flat_tax'), amount: r.capitalFlatTax, colour: 'bg-chart-3' },
  ].map((p) => ({ ...p, size: Math.max(0, toPlot(p.amount)), text: `${p.label} ${eur(p.amount)}` })).filter((p) => p.size > 0);
  if (parts.length < 2) return null;
  return (
    <figure className="space-y-2">
      <figcaption className="text-sm text-muted-foreground">{t('tax.split.title', { amount: eur(r.netBeforeTax) })}</figcaption>
      <div role="img" aria-label={parts.map((p) => p.text).join(', ')} className="flex h-3.5 gap-0.5 overflow-hidden rounded-full">
        {parts.map((p) => <div key={p.label} className={p.colour} style={{ flexGrow: p.size }} />)}
      </div>
      <ul aria-hidden="true" className="flex flex-wrap gap-x-5 gap-y-1 text-sm">
        {parts.map((p) => <li key={p.label} className="flex items-center gap-1.5"><span className={`size-2.5 rounded-sm ${p.colour}`} />{p.text}</li>)}
      </ul>
    </figure>
  );
}

/** The server's warnings and notices, in « Calcul » beside the PER block, which tells the PER warnings it can. */
function Notes({ estimate }: { estimate: TaxEstimate }) {
  const { t } = useTranslation();
  // #411: an arrangement's childcare costs link to its year summary, the figures to copy into the return.
  const notes: { text: string; childcare?: string }[] = [
    ...estimate.result.warnings.filter((w) => w !== CARRIED && !PER_WARNINGS.includes(w)).map((w) => ({ text: warningText(t, w, estimate.result.rulesYear) })),
    // The server names a PER warning once per declarant it concerns: one generic sentence for those the PER block cannot tell.
    ...[...new Set(estimate.result.warnings)].filter((w) => PER_WARNINGS.includes(w) && !perWarningsInBlock(estimate.result).has(w))
      .map((w) => ({ text: t(w === 'per_over_ceiling' ? 'tax.warning.per_over_ceiling' : 'tax.warning.per_ceiling_estimated') })),
    ...estimate.notices.map((n) => ({
      text: noticeText(t, n.code, n.name, noticeAmount(t, n.code, n.amount, eur)), childcare: n.code === 'childcare_from_statements' ? n.item : undefined,
    })),
  ];
  if (!notes.length) return null;
  return (
    <div className="flex gap-3 rounded-md border border-status-warning/60 bg-status-warning/10 p-4">
      <AlertTriangle aria-hidden="true" className="mt-1 size-4 shrink-0 text-status-warning" />
      <Section title={t('tax.notes')}>
        <ul className="list-disc space-y-1 pl-5 text-sm">
          {notes.map((n, i) => (
            <li key={`${i}-${n.text}`}>
              {n.text}
              {n.childcare ? (
                <>
                  {' '}
                  <Link to="/childcare/$id" params={{ id: n.childcare }} search={{ tab: 'summary', year: String(estimate.year) }} className="underline underline-offset-2">
                    {t('tax.childcare_summary', { year: estimate.year })}
                  </Link>
                </>
              ) : null}
            </li>
          ))}
        </ul>
      </Section>
    </div>
  );
}

function Detail({ estimate, onProfile }: { estimate: TaxEstimate; onProfile: () => void }) {
  const { t } = useTranslation();
  return (
    <>
      <p className="text-sm text-muted-foreground">{t('tax.disclaimer', { year: estimate.result.rulesYear })}</p>
      <DeclarantTable result={estimate.result} detail={estimate.salaryDetail} adults={estimate.adults}>
        <PerRows result={estimate.result} names={estimate.result.declarants.map((_, i) => (estimate.inputs.declarants[i]?.perEnvelopes ?? []).join(', '))} />
      </DeclarantTable>
      <PerSummary result={estimate.result} year={estimate.year} onProfile={onProfile} />
      <ResultTable label={t('tax.detail_table')} declarants={false} columns={[{ title: t('tax.estimate'), result: estimate.result }]} />
      <p className="text-sm text-muted-foreground">{t('tax.out_of_scope')}</p>
    </>
  );
}

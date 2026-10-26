import { useNavigate, useSearch } from '@tanstack/react-router';
import type { ReactNode } from 'react';
import { AlertTriangle } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import type { TaxSearch } from '@/app/router';
import { Segmented } from '@/components/form/Segmented';
import { DetailTabs, useDetailTab } from '@/components/page/DetailTabs';
import { PageHeader } from '@/components/page/PageHeader';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { toPlot } from '../investments/chart/plot';
import { Section } from '@/components/page/Section';
import { todayIso } from '../subscriptions/today';
import { TAX, type TaxEstimate } from './api';
import { noticeAmount, noticeText, percentText, PER_WARNINGS, warningText, ZERO } from './format';
import { PerSection, perWarningsInBlock, perWarningTexts } from './PerSection';
import { ProfileForm } from './ProfileForm';
import { DeclarantTable, eur, ResultTable } from './ResultTable';
import { SaleTotalSection } from './SaleTotalSection';
import { SimulationForm } from './SimulationForm';
import { Rental2044Section } from './Rental2044Section';
import { WithholdingSection } from './WithholdingSection';
import '@/locales/tax';

const TABS = ['detail', 'withholding', 'profile', 'simulation', '2044', 'sale'] as const;

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
  const [tab, setTab] = useDetailTab(TABS);
  // The 2044 is filed for the year just ended; the estimate opens on the current one. The default stays out of the URL.
  const defaultYear = tab === '2044' ? lastYear : thisYear;
  const years = [lastYear, thisYear].map((y) => ({ value: y, label: y }));
  // A year the page does not offer (the server refuses it) falls back to the tab's default.
  const year = years.some((y) => y.value === search.year) ? search.year! : defaultYear;
  const setYear = (next: string) => {
    void navigate({ to: '.', replace: true, search: ((prev: Record<string, unknown>) => ({ ...prev, year: next === defaultYear ? undefined : next })) as never });
  };
  const answer = $api.useQuery('get', TAX, { params: { query: { year: Number(year), scope } } });
  const estimate = answer.data;

  return (
    <div className="space-y-6">
      <PageHeader title={t('tax.title')} actions={tab === 'sale' ? undefined : <div className="print:hidden"><Segmented aria-label={t('tax.year')} value={year} onValueChange={setYear} options={years} /></div>}>
        {estimate ? <div className={tab === '2044' ? 'print:hidden' : undefined}><Figures estimate={estimate} onAdjust={() => setTab('withholding')} /></div> : null}
      </PageHeader>
      {answer.error ? <ProblemMessage problem={present(toProblem(answer.error))} /> : null}
      {!estimate && !answer.error ? <div aria-busy="true" aria-label={t('tax.loading')} className="h-40 shimmer rounded-md border bg-muted" /> : null}
      {estimate ? <Notes estimate={estimate} inBlock={tab === 'detail'} printed={tab !== '2044'} sale={tab === 'sale'} /> : null}
      {estimate ? (
        <DetailTabs value={tab} onChange={setTab} tabs={[
          { id: 'detail', label: t('tax.tab.detail'), content: <Detail estimate={estimate} onProfile={() => setTab('profile')} /> },
          { id: 'withholding', label: t('tax.tab.withholding'), content: <WithholdingSection estimate={estimate} /> },
          { id: 'profile', label: t('tax.tab.profile'), content: <ProfileForm key={`${estimate.year}-${scope}`} estimate={estimate} scope={scope} /> },
          { id: 'simulation', label: t('tax.tab.simulation'), content: <SimulationForm key={estimate.year} estimate={estimate} /> },
          { id: '2044', label: t('tax.tab.2044'), content: <Rental2044Section year={Number(year)} scope={scope} /> },
          { id: 'sale', label: t('tax.tab.sale'), content: <SaleTotalSection scope={scope} /> },
        ]} />
      ) : null}
    </div>
  );
}

/** The three figures that answer « combien, combien il reste, que faire en septembre », then the rest inline and the split. */
function Figures({ estimate, onAdjust }: { estimate: TaxEstimate; onAdjust: () => void }) {
  const { t } = useTranslation();
  const r = estimate.result;
  const balance = r.withholding.balance;
  const refund = balance.startsWith('-');
  return (
    <div className="space-y-6">
      <dl className="grid gap-6 border-b pb-6 sm:grid-cols-3">
        <Figure label={t('tax.figure.total_tax')} value={eur(r.totalTax)}
          hint={r.averageRate ? t('tax.figure.average_rate', { rate: percentText(t, r.averageRate) }) : null} />
        <Figure label={t('tax.figure.left_monthly')} value={eur(r.netAfterTaxMonthly)} hint={t('tax.figure.per_year', { amount: eur(r.netAfterTax) })} />
        <Figure label={t('tax.figure.balance', { year: estimate.year + 1 })}
          value={ZERO.test(balance) ? t('tax.figure.no_balance') : (
            <>{eur(refund ? balance.slice(1) : balance)} <span className="text-lg font-medium">{t(refund ? 'tax.figure.refund' : 'tax.figure.to_pay')}</span></>
          )}
          hint={<Button variant="link" className="h-auto p-0" onClick={onAdjust}>{t('tax.figure.adjust')}</Button>} />
      </dl>
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
      <Split result={r} />
    </div>
  );
}

function Figure({ label, value, hint }: { label: string; value: ReactNode; hint: ReactNode }) {
  return (
    <div className="space-y-1">
      <dt className="text-sm text-muted-foreground">{label}</dt>
      <dd className="text-3xl font-semibold tracking-tight">{value}</dd>
      {hint ? <dd className="text-sm text-muted-foreground">{hint}</dd> : null}
    </div>
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

/** The server's warnings and notices, above the tabs: they matter whichever tab is open. */
/** On the sale tab the estimate is always today's: the income tax's carried-forward scale is not its business (the sale section says its own). */
function Notes({ estimate, printed, sale, inBlock }: { estimate: TaxEstimate; printed: boolean; sale: boolean; /** The Détail tab, whose PER block tells the PER warnings it can. */ inBlock: boolean }) {
  const { t } = useTranslation();
  const notes = [
    ...estimate.result.warnings.filter((w) => !PER_WARNINGS.includes(w) && !(sale && w === 'rules_carried_forward')).map((w) => warningText(t, w, estimate.result.rulesYear)),
    // The server names a PER warning once per declarant it concerns; perWarningTexts already tells one sentence per declarant.
    ...[...new Set(estimate.result.warnings)].filter((w) => PER_WARNINGS.includes(w) && !(inBlock && perWarningsInBlock(estimate.result).has(w)))
      .flatMap((w) => perWarningTexts(t, estimate.result, w, estimate.year)),
    ...estimate.notices.map((n) => noticeText(t, n.code, n.name, noticeAmount(t, n.code, n.amount, eur))),
  ];
  if (!notes.length) return null;
  return (
    <div className={`flex gap-3 rounded-md border border-status-warning/60 bg-status-warning/10 p-4${printed ? '' : ' print:hidden'}`}>
      <AlertTriangle aria-hidden="true" className="mt-1 size-4 shrink-0 text-status-warning" />
      <Section title={t('tax.notes')}>
        <ul className="list-disc space-y-1 pl-5 text-sm">{notes.map((n, i) => <li key={`${i}-${n}`}>{n}</li>)}</ul>
      </Section>
    </div>
  );
}

function Detail({ estimate, onProfile }: { estimate: TaxEstimate; onProfile: () => void }) {
  const { t } = useTranslation();
  return (
    <>
      <p className="text-sm text-muted-foreground">{t('tax.disclaimer', { year: estimate.result.rulesYear })}</p>
      <DeclarantTable result={estimate.result} detail={estimate.salaryDetail} adults={estimate.adults} />
      <PerSection result={estimate.result} year={estimate.year} rulesYear={estimate.result.rulesYear} onProfile={onProfile}
        names={estimate.result.declarants.map((_, i) => (estimate.inputs.declarants[i]?.perEnvelopes ?? []).join(', '))} />
      <ResultTable label={t('tax.detail_table')} declarants={false} columns={[{ title: t('tax.estimate'), result: estimate.result }]} />
      <p className="text-sm text-muted-foreground">{t('tax.out_of_scope')}</p>
    </>
  );
}

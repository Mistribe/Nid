import { useSearch } from '@tanstack/react-router';
import { useState, type ReactNode } from 'react';
import { AlertTriangle } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import type { RootSearch } from '@/app/router';
import { Segmented } from '@/components/form/Segmented';
import { DetailTabs, useDetailTab } from '@/components/page/DetailTabs';
import { PageHeader } from '@/components/page/PageHeader';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { toPlot } from '../investments/chart/plot';
import { Section } from '@/components/page/Section';
import { todayIso } from '../subscriptions/today';
import { TAX, type TaxEstimate } from './api';
import { noticeText, percentText, warningText, ZERO } from './format';
import { ProfileForm } from './ProfileForm';
import { DeclarantTable, eur, ResultTable } from './ResultTable';
import { SimulationForm } from './SimulationForm';
import { WithholdingSection } from './WithholdingSection';
import '@/locales/tax';

const TABS = ['detail', 'withholding', 'profile', 'simulation'] as const;

/**
 * Tax estimate spec §9: the year's figures inline, then the calculation step by step, the household's profile, and a
 * simulation of edited inputs. Every figure is the server's; the page only formats them.
 */
export function TaxPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const search: RootSearch = useSearch({ strict: false });
  const scope = search.scope === 'me' ? 'me' : 'household';
  const thisYear = todayIso().slice(0, 4);
  const [year, setYear] = useState(thisYear);
  const years = [String(Number(thisYear) - 1), thisYear].map((y) => ({ value: y, label: y }));
  const answer = $api.useQuery('get', TAX, { params: { query: { year: Number(year), scope } } });
  const [tab, setTab] = useDetailTab(TABS);
  const estimate = answer.data;

  return (
    <div className="space-y-6">
      <PageHeader title={t('tax.title')} actions={<Segmented aria-label={t('tax.year')} value={year} onValueChange={setYear} options={years} />}>
        {estimate ? <Figures estimate={estimate} onAdjust={() => setTab('withholding')} /> : null}
      </PageHeader>
      {answer.error ? <ProblemMessage problem={present(toProblem(answer.error))} /> : null}
      {!estimate && !answer.error ? <div aria-busy="true" aria-label={t('tax.loading')} className="h-40 animate-pulse rounded-md border bg-muted" /> : null}
      {estimate ? <Notes estimate={estimate} /> : null}
      {estimate ? (
        <DetailTabs value={tab} onChange={setTab} tabs={[
          { id: 'detail', label: t('tax.tab.detail'), content: <Detail estimate={estimate} /> },
          { id: 'withholding', label: t('tax.tab.withholding'), content: <WithholdingSection estimate={estimate} /> },
          { id: 'profile', label: t('tax.tab.profile'), content: <ProfileForm key={`${estimate.year}-${scope}`} estimate={estimate} scope={scope} /> },
          { id: 'simulation', label: t('tax.tab.simulation'), content: <SimulationForm key={estimate.year} estimate={estimate} /> },
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
function Notes({ estimate }: { estimate: TaxEstimate }) {
  const { t } = useTranslation();
  const notes = [
    ...estimate.result.warnings.map((w) => warningText(t, w, estimate.result.rulesYear)),
    ...estimate.notices.map((n) => noticeText(t, n.code, n.name, eur(n.amount))),
  ];
  if (!notes.length) return null;
  return (
    <div className="flex gap-3 rounded-md border border-status-warning/60 bg-status-warning/10 p-4">
      <AlertTriangle aria-hidden="true" className="mt-1 size-4 shrink-0 text-status-warning" />
      <Section title={t('tax.notes')}>
        <ul className="list-disc space-y-1 pl-5 text-sm">{notes.map((n) => <li key={n}>{n}</li>)}</ul>
      </Section>
    </div>
  );
}

function Detail({ estimate }: { estimate: TaxEstimate }) {
  const { t } = useTranslation();
  return (
    <>
      <p className="text-sm text-muted-foreground">{t('tax.disclaimer', { year: estimate.result.rulesYear })}</p>
      <DeclarantTable result={estimate.result} />
      <ResultTable label={t('tax.detail_table')} declarants={false} columns={[{ title: t('tax.estimate'), result: estimate.result }]} />
      <p className="text-sm text-muted-foreground">{t('tax.out_of_scope')}</p>
    </>
  );
}

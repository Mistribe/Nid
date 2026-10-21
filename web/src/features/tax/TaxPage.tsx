import { useSearch } from '@tanstack/react-router';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import type { RootSearch } from '@/app/router';
import { Segmented } from '@/components/form/Segmented';
import { DetailTabs, useDetailTab } from '@/components/page/DetailTabs';
import { PageHeader } from '@/components/page/PageHeader';
import { StatLine } from '@/components/page/StatLine';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Section } from '../income/Section';
import { todayIso } from '../subscriptions/today';
import { TAX, type TaxEstimate } from './api';
import { noticeText, percentText, warningText } from './format';
import { ProfileForm } from './ProfileForm';
import { eur, ResultTable } from './ResultTable';
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
        {estimate ? <Figures estimate={estimate} /> : null}
      </PageHeader>
      {answer.error ? <ProblemMessage problem={present(toProblem(answer.error))} /> : null}
      {!estimate && !answer.error ? <div aria-busy="true" aria-label={t('tax.loading')} className="h-40 animate-pulse rounded-md border bg-muted" /> : null}
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

function Figures({ estimate }: { estimate: TaxEstimate }) {
  const { t } = useTranslation();
  const r = estimate.result;
  return (
    <StatLine items={[
      { label: t('tax.figure.net_taxable'), value: eur(r.netTaxableIncome) },
      { label: t('tax.figure.income_tax'), value: eur(r.incomeTax) },
      { label: t('tax.figure.total_tax'), value: eur(r.totalTax) },
      { label: t('tax.figure.net_after'), value: eur(r.netAfterTax) },
      { label: t('tax.figure.monthly'), value: eur(r.netAfterTaxMonthly) },
      { label: t('tax.figure.marginal_rate'), value: percentText(t, r.marginalRate) },
      { label: t('tax.figure.withholding_rate'), value: percentText(t, r.withholding.rate) },
    ]} />
  );
}

function Detail({ estimate }: { estimate: TaxEstimate }) {
  const { t } = useTranslation();
  const notes = [
    ...estimate.result.warnings.map((w) => warningText(t, w, estimate.result.rulesYear)),
    ...estimate.notices.map((n) => noticeText(t, n.code, n.name, eur(n.amount))),
  ];
  return (
    <>
      <p className="text-sm text-muted-foreground">{t('tax.disclaimer', { year: estimate.result.rulesYear })}</p>
      {notes.length ? (
        <Section title={t('tax.notes')}>
          <ul className="list-disc space-y-1 pl-5 text-sm">{notes.map((n) => <li key={n}>{n}</li>)}</ul>
        </Section>
      ) : null}
      <ResultTable label={t('tax.detail_table')} columns={[{ title: t('tax.estimate'), result: estimate.result }]} />
      <p className="text-sm text-muted-foreground">{t('tax.out_of_scope')}</p>
    </>
  );
}

import { keepPreviousData } from '@tanstack/react-query';
import { useNavigate, useSearch } from '@tanstack/react-router';
import { Printer } from 'lucide-react';
import { useId } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { YEAR_REVIEW, type NetWorthReview, type YearReview } from '@/api/reports';
import type { YearReviewSearch } from '@/app/router';
import { Direction } from '@/components/Direction';
import { ErrorPage } from '@/components/ErrorPage';
import { SelectField } from '@/components/form/SelectField';
import { formatLocalDate } from '@/components/LocalDate';
import { Money } from '@/components/Money';
import { PageHeader } from '@/components/page/PageHeader';
import { Section } from '@/components/page/Section';
import { PageSkeleton } from '@/components/page/skeletons';
import { StatLine } from '@/components/page/StatLine';
import { PageTrail } from '@/components/page/trail';
import { Button } from '@/components/ui/button';
import type { RootSearch } from '@/lib/search';
import { IncomeReview } from './IncomeReview';
import { InvestmentsReview } from './InvestmentsReview';
import { NetWorthBridge, signed } from './NetWorthBridge';
import '@/locales/reports';

const FX_MISSING = 'core.fx_rate_missing';

/** Issue #12: the year's review, net worth then investments, from the server's figures for the Foyer / Moi scope. */
export function YearReviewPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const navigate = useNavigate();
  const labelId = useId();
  const search: RootSearch & YearReviewSearch = useSearch({ strict: false });
  const scope = search.scope === 'me' ? 'me' : 'household';
  // Another year keeps the page on the one shown until it arrives, so the header does not jump.
  const answer = $api.useQuery('get', YEAR_REVIEW, { params: { query: { scope, year: search.year } } }, { placeholderData: keepPreviousData });
  const crumbs = [{ label: t('nav.dashboard'), to: '/dashboard' as const }];

  if (answer.error) return <ErrorPage problem={present(toProblem(answer.error))} crumbs={crumbs} onRetry={() => void answer.refetch()} />;
  if (!answer.data) return <><PageTrail crumbs={crumbs} /><PageSkeleton label={t('reports.loading')} actions={2} stats={3} /></>;

  const d = answer.data;
  // The server's current year (the one it reviews to date, else the latest it offers) is the default: it stays out of the URL.
  const current = d.toDate ? d.year : Math.max(...d.years);
  // The root keeps Foyer / Moi.
  const setYear = (next: string) => void navigate({
    to: '/reports/year', replace: true, resetScroll: false, search: { year: Number(next) === current ? undefined : Number(next) },
  });
  const actions = (
    <>
      <span id={labelId} className="sr-only">{t('reports.year')}</span>
      <div className="w-28 print:hidden">
        <SelectField id={`${labelId}-control`} aria-labelledby={labelId} value={String(d.year)} onValueChange={setYear}
          options={d.years.map((y) => ({ value: String(y), label: String(y) }))} />
      </div>
      <Button type="button" variant="outline" className="print:hidden" onClick={() => window.print()}>
        <Printer aria-hidden="true" />{t('reports.print')}
      </Button>
    </>
  );
  return (
    <div className="space-y-6">
      <PageHeader crumbs={crumbs} title={t(d.toDate ? 'reports.title_to_date' : 'reports.title', { year: d.year })} actions={actions} />
      {d.netWorth ? <NetWorth d={d} review={d.netWorth} />
        : d.findings.includes(FX_MISSING) ? <p className="text-sm text-muted-foreground">{t('reports.fx_missing')}</p> : null}
      {d.investments ? <InvestmentsReview year={d.year} review={d.investments} /> : null}
      {d.income ? <IncomeReview year={d.year} review={d.income} /> : null}
      {!d.netWorth && !d.investments && !d.income && d.findings.length === 0 ? <p className="text-muted-foreground">{t('reports.empty')}</p> : null}
    </div>
  );
}

function NetWorth({ d, review }: { d: YearReview; review: NetWorthReview }) {
  const { t } = useTranslation();
  return (
    <Section title={t('reports.net_worth.title')}>
      <StatLine items={[
        { label: t('reports.net_worth.at', { date: formatLocalDate(d.start, 'short') }), value: <Money value={review.start} tabular /> },
        { label: t('reports.net_worth.at', { date: formatLocalDate(d.end, 'short') }), value: <Money value={review.end} tabular /> },
        { label: t('reports.net_worth.change'), value: <Direction amount={review.change} />,
          hint: review.previousChange ? t('reports.previous', { year: d.year - 1, amount: signed(review.previousChange) }) : null },
      ]} />
      <NetWorthBridge year={d.year} start={review.start} parts={review.parts} previousParts={review.previousParts} end={review.end} />
    </Section>
  );
}

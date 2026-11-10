import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import type { EngagementReviewSource, IncomeReview as Review, IncomeReviewSource } from '@/api/reports';
import { Direction } from '@/components/Direction';
import { formatMoney, Money, type MoneyValue } from '@/components/Money';
import { Section } from '@/components/page/Section';
import { SortableTable } from '@/components/page/SortableTable';
import { amountKey } from '@/components/page/sortKey';
import { StatLine } from '@/components/page/StatLine';
import { kindText } from '@/features/income/format';
import { signed } from './NetWorthBridge';
import '@/locales/income';
import '@/locales/reports';

const COMMITMENTS = ['subscriptions', 'loans', 'property_costs', 'tax', 'childcare'] as const;
/** The postings whose label already says « estimé »: no second mark. */
const ALREADY_ESTIMATED = new Set(['tax']);

/** The year's income, engagements and margin from the server's figures, beside the year before, then where each comes from. */
export function IncomeReview({ year, review }: { year: number; review: Review }) {
  const { t } = useTranslation();
  const unknown = <span className="text-muted-foreground">{t('reports.income.unknown')}</span>;
  const money = (value?: MoneyValue | null): ReactNode => (value ? <Money value={value} tabular /> : unknown);
  // Only the margin can go either way: the year before's income and engagements read plainly.
  const before = (value?: MoneyValue | null, format: (v: MoneyValue) => string = formatMoney) =>
    (value ? t('reports.previous', { year: year - 1, amount: format(value) }) : null);
  const source = (r: IncomeReviewSource) => (r.key === 'one_off' ? t('income.overview.one_off')
    : r.name ?? (r.kind === 'rent' ? t('income.overview.kind_rent') : r.kind ? kindText(t, r.kind) : r.key));
  const posting = (r: EngagementReviewSource): string => {
    const known = COMMITMENTS.find((c) => c === r.source);
    return known ? t(`dashboard.commitment_name.${known}`) : r.source;
  };
  const engagement = (r: EngagementReviewSource) => (
    <span className="inline-flex flex-wrap items-baseline gap-x-2">
      {posting(r)}
      {r.estimated && !ALREADY_ESTIMATED.has(r.source) ? <> <span className="text-xs text-muted-foreground">{t('reports.income.estimated')}</span></> : null}
    </span>
  );
  return (
    <Section title={t('reports.income.title')}>
      <StatLine items={[
        { label: t('reports.income.income'), value: money(review.income), hint: before(review.previousIncome) },
        { label: t('reports.income.engagements'), value: money(review.engagements), hint: before(review.previousEngagements) },
        { label: t('reports.income.margin'), value: review.margin ? <Direction amount={review.margin} /> : unknown, hint: before(review.previousMargin, signed) },
      ]} />
      <SortableTable<IncomeReviewSource> caption={t('reports.income.sources')} rows={review.sources} rowKey={(r) => r.key}
        columns={[
          { id: 'name', header: t('reports.income.col.source'), sort: (r) => source(r), cell: (r) => source(r) },
          { id: 'amount', header: t('reports.income.col.amount'), align: 'end', sort: (r) => amountKey(r.amount?.amount), cell: (r) => money(r.amount) },
        ]}
        mobile={(r) => <div className="flex items-baseline justify-between gap-3"><span>{source(r)}</span><span className="tabular-nums">{money(r.amount)}</span></div>} />
      <SortableTable<EngagementReviewSource> caption={t('reports.income.engagement_sources')} rows={review.engagementSources} rowKey={(r) => r.source}
        columns={[
          { id: 'name', header: t('reports.income.col.source'), sort: (r) => posting(r), cell: engagement },
          { id: 'amount', header: t('reports.income.col.amount'), align: 'end', sort: (r) => amountKey(r.amount?.amount), cell: (r) => money(r.amount) },
        ]}
        mobile={(r) => <div className="flex items-baseline justify-between gap-3">{engagement(r)}<span className="tabular-nums">{money(r.amount)}</span></div>} />
    </Section>
  );
}

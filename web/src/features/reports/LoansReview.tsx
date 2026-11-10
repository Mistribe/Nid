import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { useSession } from '@/app/Providers';
import type { LoanReviewRow, LoansReview as Review } from '@/api/reports';
import { formatLocalDate } from '@/components/LocalDate';
import { formatMoney, Money, type MoneyValue } from '@/components/Money';
import { Section } from '@/components/page/Section';
import { SortableTable } from '@/components/page/SortableTable';
import { amountKey } from '@/components/page/sortKey';
import { StatLine } from '@/components/page/StatLine';
import '@/locales/loans';
import '@/locales/reports';

/** The year's loans: what they cost and repaid, their balance at both ends, beside the year before, then one row per loan. */
export function LoansReview({ year, review }: { year: number; review: Review }) {
  const { t } = useTranslation();
  const { capabilities } = useSession();
  const unknown = <span className="text-muted-foreground">{t('reports.loans.unknown')}</span>;
  const money = (value?: MoneyValue | null): ReactNode => (value ? <Money value={value} tabular /> : unknown);
  const figures = [
    { id: 'interest', label: t('loan_detail.schedule.col_interest'), of: (r: Review | LoanReviewRow) => r.interest },
    { id: 'insurance', label: t('loan_detail.schedule.col_insurance'), of: (r: Review | LoanReviewRow) => r.insurance },
    { id: 'fees', label: t('loan_detail.fees'), of: (r: Review | LoanReviewRow) => r.fees },
    { id: 'capital', label: t('loan_detail.principal_repaid'), of: (r: Review | LoanReviewRow) => r.capitalRepaid },
    { id: 'start', label: t('reports.loans.start_balance'), of: (r: Review | LoanReviewRow) => r.startBalance },
    { id: 'end', label: t('reports.loans.end_balance'), of: (r: Review | LoanReviewRow) => r.endBalance },
  ];
  // The year before gives interest and insurance as one cost of credit: said beside the interest.
  const hints: Record<string, string | null> = {
    interest: review.previousInterestAndInsurance
      ? t('reports.loans.previous_with_insurance', { year: year - 1, amount: formatMoney(review.previousInterestAndInsurance) }) : null,
    capital: review.previousCapitalRepaid ? t('reports.previous', { year: year - 1, amount: formatMoney(review.previousCapitalRepaid) }) : null,
  };
  const name = (r: LoanReviewRow) => (
    <span className="inline-flex flex-wrap items-baseline gap-x-2">
      {r.name}
      {/* A space, so a screen reader does not run the name into the mark. */}
      {r.archivedOn ? <> <span className="text-xs text-muted-foreground">{t('reports.loans.archived_on', { date: formatLocalDate(r.archivedOn) })}</span></> : null}
    </span>
  );
  return (
    <Section title={t('reports.loans.title')}>
      <StatLine items={figures.map((f) => ({ label: f.label, value: money(f.of(review)), hint: hints[f.id] ?? null }))} />
      <SortableTable<LoanReviewRow> caption={t('reports.loans.table')} rows={review.loans} rowKey={(r) => r.id}
        rowLink={capabilities.modules.includes('realestate') ? (r) => ({ to: '/loans/$id', params: { id: r.id } }) : undefined}
        columns={[
          { id: 'name', header: t('reports.loans.col_name'), sort: (r) => r.name, cell: name },
          ...figures.map((f) => ({ id: f.id, header: f.label, align: 'end' as const, sort: (r: LoanReviewRow) => amountKey(f.of(r)?.amount),
            cell: (r: LoanReviewRow) => money(f.of(r)) })),
        ]}
        mobile={(r) => <div className="flex items-baseline justify-between gap-3">{name(r)}<span className="tabular-nums">{money(r.endBalance)}</span></div>} />
    </Section>
  );
}

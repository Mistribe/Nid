import { useTranslation } from 'react-i18next';
import type { TaxReview as Review } from '@/api/reports';
import { formatMoney, Money, type MoneyValue } from '@/components/Money';
import { Section } from '@/components/page/Section';
import { StatLine } from '@/components/page/StatLine';
import '@/locales/reports';
import '@/locales/tax';

/** The year's estimated tax beside the year before, then its parts; an estimate from the entered data, not a notice. */
export function TaxReview({ year, review }: { year: number; review: Review }) {
  const { t } = useTranslation();
  const money = (value?: MoneyValue | null) => (value ? <Money value={value} tabular /> : <span className="text-muted-foreground">{t('reports.tax.unknown')}</span>);
  return (
    <Section title={t('reports.tax.title')}>
      <StatLine items={[
        { label: t('reports.tax.total'), value: money(review.totalTax),
          hint: review.previousTotalTax ? t('reports.previous', { year: year - 1, amount: formatMoney(review.previousTotalTax) }) : null },
        { label: t('tax.row.income_tax'), value: money(review.incomeTax) },
        { label: t('tax.row.social_levies'), value: money(review.socialLevies) },
        { label: t('tax.row.flat_tax'), value: money(review.capitalFlatTax) },
      ]} />
      <p className="text-sm text-muted-foreground">{t('reports.tax.estimate')}</p>
    </Section>
  );
}

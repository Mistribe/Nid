import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { useSession } from '@/app/Providers';
import type { PropertiesReview as Review, PropertyReviewRow } from '@/api/reports';
import { formatMoney, Money, type MoneyValue } from '@/components/Money';
import { Section } from '@/components/page/Section';
import { SortableTable } from '@/components/page/SortableTable';
import { amountKey } from '@/components/page/sortKey';
import { StatLine } from '@/components/page/StatLine';
import { categoryText } from '@/features/properties/format';
import '@/locales/properties';
import '@/locales/reports';

type CategoryRow = { key: string; id: string; name: string; category: string; amount?: MoneyValue | null };

const decimal = new Intl.NumberFormat('fr-FR', { minimumFractionDigits: 1, maximumFractionDigits: 1 });

/** The year's properties: costs and rents beside the year before, one row per property with its yields, then the costs by category. */
export function PropertiesReview({ year, review }: { year: number; review: Review }) {
  const { t } = useTranslation();
  const { capabilities } = useSession();
  const unknown = <span className="text-muted-foreground">{t('reports.properties.unknown')}</span>;
  const money = (value?: MoneyValue | null): ReactNode => (value ? <Money value={value} tabular /> : unknown);
  const before = (value?: MoneyValue | null) => (value ? t('reports.previous', { year: year - 1, amount: formatMoney(value) }) : null);
  // Null when the property was not let before the period began.
  const yieldText = (value?: number | null) => (value == null ? t('properties_rental.not_significant') : t('reports.properties.pct', { value: decimal.format(value) }));
  const rowLink = capabilities.modules.includes('realestate')
    ? (r: { id: string }) => ({ to: '/properties/$id' as const, params: { id: r.id } }) : undefined;
  const categories: CategoryRow[] = review.properties.flatMap((p) => p.byCategory.map((c) => ({ key: `${p.id}-${c.category}`, id: p.id, name: p.name, ...c })));
  return (
    <Section title={t('reports.properties.title')}>
      <StatLine items={[
        { label: t('reports.properties.costs'), value: money(review.costs), hint: before(review.previousCosts) },
        { label: t('reports.properties.rents'), value: money(review.rents), hint: before(review.previousRents) },
      ]} />
      <SortableTable<PropertyReviewRow> caption={t('reports.properties.table')} rows={review.properties} rowKey={(r) => r.id} rowLink={rowLink}
        columns={[
          { id: 'name', header: t('reports.properties.col.name'), sort: (r) => r.name, cell: (r) => r.name },
          { id: 'costs', header: t('reports.properties.costs'), align: 'end', sort: (r) => amountKey(r.costs?.amount), cell: (r) => money(r.costs) },
          { id: 'rents', header: t('reports.properties.rents'), align: 'end', sort: (r) => amountKey(r.rents?.amount), cell: (r) => money(r.rents) },
          { id: 'gross', header: t('property_rental.gross_yield'), align: 'end', sort: (r) => r.grossYieldPct ?? Number.NEGATIVE_INFINITY,
            cell: (r) => yieldText(r.grossYieldPct) },
          { id: 'net', header: t('property_rental.net_yield'), align: 'end', sort: (r) => r.netYieldPct ?? Number.NEGATIVE_INFINITY,
            cell: (r) => yieldText(r.netYieldPct) },
        ]}
        mobile={(r) => <div className="flex items-baseline justify-between gap-3"><span>{r.name}</span><span className="tabular-nums">{yieldText(r.grossYieldPct)}</span></div>} />
      {categories.length === 0 ? null : (
        <SortableTable<CategoryRow> caption={t('reports.properties.by_category')} rows={categories} rowKey={(r) => r.key} rowLink={rowLink}
          columns={[
            { id: 'name', header: t('reports.properties.col.name'), sort: (r) => r.name, cell: (r) => r.name },
            { id: 'category', header: t('reports.properties.col.category'), sort: (r) => categoryText(t, r.category), cell: (r) => categoryText(t, r.category) },
            { id: 'amount', header: t('reports.properties.col.amount'), align: 'end', sort: (r) => amountKey(r.amount?.amount), cell: (r) => money(r.amount) },
          ]}
          mobile={(r) => (
            <div className="flex items-baseline justify-between gap-3"><span>{r.name} · {categoryText(t, r.category)}</span><span className="tabular-nums">{money(r.amount)}</span></div>
          )} />
      )}
    </Section>
  );
}

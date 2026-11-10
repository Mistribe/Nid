import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { useSession } from '@/app/Providers';
import type { SubscriptionPriceChange, SubscriptionReviewItem, SubscriptionsReview as Review } from '@/api/reports';
import { formatLocalDate } from '@/components/LocalDate';
import { formatMoney, Money } from '@/components/Money';
import { Section } from '@/components/page/Section';
import { SortableTable } from '@/components/page/SortableTable';
import { StatLine } from '@/components/page/StatLine';
import { durationText } from '@/features/subscriptions/format';
import '@/locales/reports';

/** The year's subscriptions: the server's cost beside the year before, then those started, ended and whose price changed. */
export function SubscriptionsReview({ year, review }: { year: number; review: Review }) {
  const { t } = useTranslation();
  const { capabilities } = useSession();
  const rowLink = capabilities.modules.includes('subscriptions')
    ? (r: { id: string }) => ({ to: '/subscriptions/$id' as const, params: { id: r.id } }) : undefined;
  const items = (caption: string, rows: SubscriptionReviewItem[]) => (rows.length === 0 ? null : (
    <SortableTable<SubscriptionReviewItem> caption={caption} rows={rows} rowKey={(r) => r.id} rowLink={rowLink}
      columns={[
        { id: 'name', header: t('reports.subscriptions.col.name'), sort: (r) => r.name, cell: (r) => r.name },
        { id: 'date', header: t('reports.subscriptions.col.date'), sort: (r) => r.on, cell: (r) => formatLocalDate(r.on) },
      ]}
      mobile={(r) => <div className="flex items-baseline justify-between gap-3"><span>{r.name}</span><span>{formatLocalDate(r.on)}</span></div>} />
  ));
  const price = (r: SubscriptionPriceChange) => t('reports.subscriptions.price_change', {
    before: t('reports.subscriptions.price', { amount: formatMoney(r.before), every: durationText(t, r.beforeEvery) }),
    after: t('reports.subscriptions.price', { amount: formatMoney(r.after), every: durationText(t, r.afterEvery) }),
  });
  const name = (r: SubscriptionPriceChange): ReactNode => (
    <span className="inline-flex flex-wrap items-baseline gap-x-2">
      {r.name}
      {/* A space, so a screen reader does not run the name into the mark. */}
      {r.promoEnded ? <> <span className="text-xs text-muted-foreground">{t('reports.subscriptions.promo_ended')}</span></> : null}
    </span>
  );
  return (
    <Section title={t('reports.subscriptions.title')}>
      <StatLine items={[{
        label: t('reports.subscriptions.cost'),
        value: review.cost ? <Money value={review.cost} tabular /> : <span className="text-muted-foreground">{t('reports.subscriptions.unknown')}</span>,
        hint: review.previousCost ? t('reports.previous', { year: year - 1, amount: formatMoney(review.previousCost) }) : null,
      }]} />
      {items(t('reports.subscriptions.started'), review.started)}
      {items(t('reports.subscriptions.ended'), review.ended)}
      {review.priceChanges.length === 0 ? null : (
        <SortableTable<SubscriptionPriceChange> caption={t('reports.subscriptions.price_changes')} rows={review.priceChanges}
          rowKey={(r) => `${r.id}-${r.on}`} rowLink={rowLink}
          columns={[
            { id: 'name', header: t('reports.subscriptions.col.name'), sort: (r) => r.name, cell: name },
            { id: 'date', header: t('reports.subscriptions.col.date'), sort: (r) => r.on, cell: (r) => formatLocalDate(r.on) },
            { id: 'price', header: t('reports.subscriptions.col.price'), cell: price },
          ]}
          mobile={(r) => <div className="space-y-1">{name(r)}<div className="text-sm text-muted-foreground">{formatLocalDate(r.on)} · {price(r)}</div></div>} />
      )}
    </Section>
  );
}

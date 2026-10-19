import { useId } from 'react';
import { useTranslation } from 'react-i18next';
import { formatMoney } from '@/components/Money';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { toPlot } from '../investments/chart/plot';
import type { DashboardView } from './api';

const SOURCES = [
  { key: 'subscriptions', color: 'var(--chart-1)' },
  { key: 'loans', color: 'var(--chart-2)' },
  { key: 'property_costs', color: 'var(--chart-3)' },
] as const;

/** Spec §4.1: the month's income as one stacked bar, a legend with every amount the server gave. */
export function HouseholdMonth({ d }: { d: DashboardView }) {
  const { t } = useTranslation();
  const id = useId();
  const segments = [
    ...d.commitmentSources.flatMap((s) => {
      const source = SOURCES.find((x) => x.key === s.source);
      return source && s.amortized ? [{ key: source.key, label: t(`dashboard.commitment_source.${source.key}`, { amount: formatMoney(s.amortized) }), color: source.color, size: toPlot(s.amortized.amount) }] : [];
    }),
    ...(d.margin ? [{ key: 'rest', label: t('dashboard.month.rest', { amount: formatMoney(d.margin) }), color: 'var(--chart-6)', size: toPlot(d.margin.amount) }] : []),
  ];
  // The bar is the income: without it the segments have no whole to be a share of.
  const known = d.income.total != null && d.margin != null && segments.length > 0;
  const missing = d.income.missingCurrency ?? d.commitments.missingCurrency;
  // Income unknown, else margin unknown (a commitment misses a rate): a bar without its « Reste » would look complete.
  const reason = d.income.total == null
    ? (d.income.missingCurrency ? t('dashboard.unknown_rate', { code: d.income.missingCurrency }) : t('dashboard.income_unknown'))
    : (missing ? t('dashboard.margin_unknown_rate', { code: missing }) : t('dashboard.margin_unknown'));
  return (
    <Card role="region" aria-labelledby={id} data-guide="dashboard.month" className="gap-3 py-4">
      <CardHeader className="flex flex-wrap items-baseline justify-between gap-x-4">
        <CardTitle id={id} className="text-base">{t('dashboard.month.title')}</CardTitle>
        {d.cashThisMonth.total ? <span className="text-sm text-muted-foreground">{t('dashboard.cash_this_month', { amount: formatMoney(d.cashThisMonth.total) })}</span> : null}
      </CardHeader>
      <CardContent className="space-y-3">
        {known ? (
          <>
            {/* Widths are the amounts' plotting values as flex-grow: the browser shares the bar, we do no arithmetic. */}
            <div aria-hidden="true" className="flex h-3 w-full overflow-hidden rounded-full bg-muted">
              {segments.filter((s) => s.size > 0).map((s) => (
                <div key={s.key} data-segment={s.key} style={{ flexGrow: s.size, flexBasis: 0, background: s.color }} />
              ))}
            </div>
            <ul className="flex flex-wrap gap-x-5 gap-y-1 text-sm">
              {segments.map((s) => (
                <li key={s.key} className="flex items-center gap-2">
                  <span aria-hidden="true" className="size-2.5 rounded-sm" style={{ background: s.color }} />
                  <span className="tabular-nums">{s.label}</span>
                </li>
              ))}
            </ul>
          </>
        ) : (
          <p className="text-sm text-muted-foreground">
            {reason}
          </p>
        )}
      </CardContent>
    </Card>
  );
}

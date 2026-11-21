import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Bar, CartesianGrid, ComposedChart, Line, ReferenceLine, XAxis, YAxis } from 'recharts';
import { formatCurrency } from '@/components/Money';
import { SectionCard } from '@/components/page/SectionCard';
import { SortableTable } from '@/components/page/SortableTable';
import { TableToggle } from '@/components/page/TableToggle';
import { ChartContainer, ChartLegend, ChartLegendContent, ChartTooltip, type ChartConfig } from '@/components/ui/chart';
import { monthLabel } from '@/lib/format';
import { toPlot } from '@/lib/plot';
import type { Spending } from './api';
import '@/locales/spending';

type Month = Spending['months'][number];
const SERIES = ['income', 'committed', 'everyday', 'left'] as const;
type Series = (typeof SERIES)[number];
type Row = { key: string; label: string; month: Month; income: number | null; committed: number | null; everyday: number | null; left: number | null };

const label = (m: string, style: 'long' | 'short') => monthLabel(Number(m.slice(0, 4)), Number(m.slice(5, 7)), style);
const plot = (amount: string | null | undefined) => (amount ? toPlot(amount) : null);

/**
 * #428: the period month by month (dataviz.md §2), what came in against what went out: commitments and everyday spending stacked,
 * income and what is left as lines. Every figure read is the server's (tooltip, table); an unknown one leaves a gap.
 */
export function MonthsChart({ months, currency }: { months: Month[]; currency: string }) {
  const { t } = useTranslation();
  const [asTable, setAsTable] = useState(false);
  // Accueil's colours: everyday spending slot 7, « Reste » slot 6; income in the text tone, as Biens' cash-flow line.
  const config = {
    committed: { label: t('spending.left.committed'), color: 'var(--chart-2)' },
    everyday: { label: t('spending.left.everyday'), color: 'var(--chart-7)' },
    income: { label: t('spending.left.income'), color: 'var(--foreground)' },
    left: { label: t('spending.col_left'), color: 'var(--chart-6)' },
  } satisfies ChartConfig;
  const rows: Row[] = months.map((m) => ({
    key: m.month, label: label(m.month, 'short'), month: m,
    income: plot(m.income), committed: plot(m.committed), everyday: plot(m.everyday), left: plot(m.left),
  }));
  // Display of the plotting number only: the axis is a scale, not a figure the person reads exactly.
  const axis = new Intl.NumberFormat('fr-FR', { style: 'currency', currency, notation: 'compact' });
  // No income declared reads « inconnu », never 0.
  const text = (m: Month, id: Series) => (m[id] ? formatCurrency(m[id], currency) : id === 'income' ? t('spending.left.unknown') : '—');

  return (
    <SectionCard title={t('spending.months_title')} actions={<TableToggle asTable={asTable} onToggle={() => setAsTable((v) => !v)} />}>
      {asTable ? (
        <SortableTable<Month> caption={t('spending.months_title')} rows={months} rowKey={(m) => m.month}
          columns={[
            { id: 'month', header: t('spending.col_month'), cell: (m) => <span className="first-letter:uppercase">{label(m.month, 'long')}</span> },
            ...SERIES.map((id) => ({ id, header: config[id].label, align: 'end' as const, cell: (m: Month) => text(m, id) })),
          ]}
          mobile={(m) => (
            <div className="space-y-1">
              <div className="font-medium first-letter:uppercase">{label(m.month, 'long')}</div>
              <dl className="grid grid-cols-2 gap-x-4 text-sm">
                {SERIES.map((id) => (
                  <div key={id} className="flex justify-between gap-2"><dt className="text-muted-foreground">{config[id].label}</dt><dd className="tabular-nums">{text(m, id)}</dd></div>
                ))}
              </dl>
            </div>
          )} />
      ) : (
        <ChartContainer config={config} className="h-64 w-full overflow-hidden">
          <ComposedChart data={rows} title={t('spending.months_chart')} accessibilityLayer maxBarSize={24} margin={{ top: 8, right: 8 }}>
            <CartesianGrid vertical={false} />
            <XAxis dataKey="label" tickLine={false} tickMargin={8} minTickGap={24} />
            <YAxis width={64} tickLine={false} axisLine={false} tickFormatter={(n: number) => axis.format(n)} />
            <ReferenceLine y={0} stroke="var(--border)" />
            <ChartTooltip cursor={false} content={<MonthTooltip text={text} labels={config} />} />
            <ChartLegend content={<ChartLegendContent />} />
            <Bar dataKey="committed" stackId="out" fill="var(--color-committed)" stroke="var(--card)" strokeWidth={2} animationDuration={800} />
            <Bar dataKey="everyday" stackId="out" fill="var(--color-everyday)" stroke="var(--card)" strokeWidth={2} radius={[4, 4, 0, 0]} animationDuration={800} />
            <Line dataKey="income" stroke="var(--color-income)" strokeWidth={2} type="linear" dot={false} connectNulls={false} animationDuration={800} />
            <Line dataKey="left" stroke="var(--color-left)" strokeWidth={2} type="linear" dot={{ r: 4 }} connectNulls={false} animationDuration={800} />
          </ComposedChart>
        </ChartContainer>
      )}
    </SectionCard>
  );
}

function MonthTooltip({ active, payload, text, labels }: {
  active?: boolean; payload?: ReadonlyArray<{ payload?: Row }>; text: (m: Month, id: Series) => string; labels: Record<Series, { label: string }>;
}) {
  const row = payload?.[0]?.payload;
  if (!active || !row) return null;
  return (
    <div className="grid min-w-44 gap-1 rounded-lg border bg-popover/95 px-3 py-2 text-xs shadow-raised backdrop-blur-sm">
      <div className="font-medium first-letter:uppercase">{label(row.month.month, 'long')}</div>
      {SERIES.map((id) => (
        <div key={id} className="flex justify-between gap-3"><span className="text-muted-foreground">{labels[id].label}</span><span className="tabular-nums">{text(row.month, id)}</span></div>
      ))}
    </div>
  );
}

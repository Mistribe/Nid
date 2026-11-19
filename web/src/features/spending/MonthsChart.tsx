import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Bar, BarChart, CartesianGrid, XAxis, YAxis } from 'recharts';
import { formatCurrency } from '@/components/Money';
import { SectionCard } from '@/components/page/SectionCard';
import { SortableTable } from '@/components/page/SortableTable';
import { TableToggle } from '@/components/page/TableToggle';
import { ChartContainer, ChartTooltip, type ChartConfig } from '@/components/ui/chart';
import { monthLabel } from '@/lib/format';
import { toPlot } from '@/lib/plot';
import type { Spending } from './api';
import '@/locales/spending';

type Month = Spending['months'][number];
type Row = { key: string; label: string; month: Month; total: number };

const label = (m: string, style: 'long' | 'short') => monthLabel(Number(m.slice(0, 4)), Number(m.slice(5, 7)), style);

/** The period month by month (dataviz.md §2): one series in the accent, the server's figure in the tooltip and the table. */
export function MonthsChart({ months, currency }: { months: Month[]; currency: string }) {
  const { t } = useTranslation();
  const [asTable, setAsTable] = useState(false);
  const config = { total: { label: t('spending.total'), color: 'var(--primary)' } } satisfies ChartConfig;
  const rows: Row[] = months.map((m) => ({ key: m.month, label: label(m.month, 'short'), month: m, total: toPlot(m.total) }));
  // Display of the plotting number only: the axis is a scale, not a figure the person reads exactly.
  const axis = new Intl.NumberFormat('fr-FR', { style: 'currency', currency, notation: 'compact' });

  return (
    <SectionCard title={t('spending.months_title')} actions={<TableToggle asTable={asTable} onToggle={() => setAsTable((v) => !v)} />}>
      {asTable ? (
        <SortableTable<Month> caption={t('spending.months_title')} rows={months} rowKey={(m) => m.month}
          columns={[
            { id: 'month', header: t('spending.col_month'), cell: (m) => <span className="first-letter:uppercase">{label(m.month, 'long')}</span> },
            { id: 'total', header: t('spending.total'), align: 'end', cell: (m) => formatCurrency(m.total, currency) },
          ]}
          mobile={(m) => (
            <div className="flex justify-between gap-2"><span className="first-letter:uppercase">{label(m.month, 'long')}</span><span className="tabular-nums">{formatCurrency(m.total, currency)}</span></div>
          )} />
      ) : (
        <ChartContainer config={config} className="h-48 w-full overflow-hidden">
          <BarChart data={rows} title={t('spending.months_chart')} accessibilityLayer>
            <CartesianGrid vertical={false} />
            <XAxis dataKey="label" tickLine={false} tickMargin={8} minTickGap={24} />
            <YAxis width={64} tickLine={false} axisLine={false} tickFormatter={(n: number) => axis.format(n)} />
            <ChartTooltip cursor={false} content={<MonthTooltip currency={currency} />} />
            <Bar dataKey="total" fill="var(--color-total)" maxBarSize={24} radius={[4, 4, 0, 0]} animationDuration={800} />
          </BarChart>
        </ChartContainer>
      )}
    </SectionCard>
  );
}

function MonthTooltip({ active, payload, currency }: { active?: boolean; payload?: ReadonlyArray<{ payload?: Row }>; currency: string }) {
  const row = payload?.[0]?.payload;
  if (!active || !row) return null;
  return (
    <div className="grid min-w-36 gap-1 rounded-lg border bg-popover/95 px-3 py-2 text-xs shadow-raised backdrop-blur-sm">
      <div className="font-medium first-letter:uppercase">{label(row.month.month, 'long')}</div>
      <div className="tabular-nums">{formatCurrency(row.month.total, currency)}</div>
    </div>
  );
}

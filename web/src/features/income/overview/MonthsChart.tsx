import { useState, type ComponentProps } from 'react';
import { useTranslation } from 'react-i18next';
import { Bar, BarChart, CartesianGrid, Cell, XAxis, YAxis } from 'recharts';
import { formatMoney } from '@/components/Money';
import { SectionCard } from '@/components/page/SectionCard';
import { ChartContainer, ChartLegend, ChartLegendContent, ChartTooltip, type ChartConfig } from '@/components/ui/chart';
import { toPlot } from '@/lib/plot';
import type { IncomeOverview } from '@/api/income';
import { monthLabel } from '@/lib/format';
import { MonthsTable } from './MonthsTable';
import { TableToggle } from '@/components/page/TableToggle';

type Months = IncomeOverview['months'];
type Month = Months[number];
type Row = { key: string; label: string; month: Month; recurring?: number; oneOff?: number; previous?: number };

const UPCOMING_OPACITY = 0.4;
const plot = (v?: { amount: string } | null) => (v ? toPlot(v.amount) : undefined);

/** Month by month: this period's cash as stacked bars (upcoming months dimmed) in front of last year's total. */
export function MonthsChart({ months, missingRates, period, year }: {
  months: Months; missingRates: string[]; period: 'year' | '12m' | '3y'; year: number;
}) {
  const { t } = useTranslation();
  const [asTable, setAsTable] = useState(false);
  const config = {
    // #380: the pale shade is the colour itself, so the legend's swatch reads as the bars do.
    previous: { label: t('income.overview.col_previous'), color: 'color-mix(in oklab, var(--muted-foreground) 30%, transparent)' },
    recurring: { label: t('income.overview.col_recurring'), color: 'var(--chart-1)' },
    oneOff: { label: t('income.overview.col_one_off'), color: 'var(--chart-4)' },
  } satisfies ChartConfig;
  const rows: Row[] = months.map((m) => ({
    key: `${m.year}-${m.month}`, label: monthLabel(m.year, m.month, 'short'), month: m,
    // A month without a total (missing rate) draws nothing at all.
    recurring: m.total ? plot(m.recurring) : undefined, oneOff: m.total ? plot(m.oneOff) : undefined, previous: plot(m.previousYear),
  }));
  const currency = months[0]?.total?.currency ?? months[0]?.previousYear?.currency ?? 'EUR';
  // Display of the plotting number only: the axis is a scale, not a figure the person reads exactly.
  const axis = new Intl.NumberFormat('fr-FR', { style: 'currency', currency, notation: 'compact' });
  const title = period === 'year'
    ? t('income.overview.months_title', { year, previous: year - 1 })
    : t('income.overview.months_title_previous');
  const dim = (r: Row) => (r.month.upcoming ? UPCOMING_OPACITY : 1);
  // 36 months: thinner bars on a minimum width that scrolls sideways on a phone (spec §7).
  const wide = period === '3y';
  const [back, front] = wide ? [14, 8] : [28, 16];

  return (
    <SectionCard title={title} actions={
      <TableToggle asTable={asTable} onToggle={() => setAsTable((v) => !v)} />
    }>
        {asTable
          ? <MonthsTable months={months} missingRates={missingRates} />
          : (
            // Only the 36 months scroll: a scroll box around a chart that fits shows a bar for its sub-pixel overflow.
            <div className={wide ? 'overflow-x-auto overflow-y-hidden' : undefined}>
              <ChartContainer config={config} className={wide ? 'h-64 w-full min-w-[48rem] overflow-hidden' : 'h-64 w-full overflow-hidden'}>
                <BarChart data={rows} title={t('income.overview.months_chart')} accessibilityLayer>
                  <CartesianGrid vertical={false} />
                  <XAxis xAxisId="now" dataKey="label" tickLine={false} tickMargin={8} minTickGap={24} />
                  <XAxis xAxisId="prev" dataKey="label" hide />
                  <YAxis width={64} tickLine={false} axisLine={false} tickFormatter={(n: number) => axis.format(n)} />
                  <ChartTooltip cursor={false} content={<MonthTooltip />} />
                  <ChartLegend content={<MonthsLegend upcoming={rows.filter((r) => r.month.upcoming).map((r) => r.label)} />} />
                  <Bar xAxisId="prev" dataKey="previous" fill="var(--color-previous)" barSize={back} radius={[4, 4, 0, 0]} animationDuration={800} />
                  <Bar xAxisId="now" dataKey="recurring" stackId="now" fill="var(--color-recurring)" barSize={front} animationDuration={800}>
                    {rows.map((r) => <Cell key={r.key} fillOpacity={dim(r)} />)}
                  </Bar>
                  <Bar xAxisId="now" dataKey="oneOff" stackId="now" fill="var(--color-oneOff)" barSize={front} radius={[4, 4, 0, 0]} animationDuration={800}>
                    {rows.map((r) => <Cell key={r.key} fillOpacity={dim(r)} />)}
                  </Bar>
                </BarChart>
              </ChartContainer>
            </div>
          )}
    </SectionCard>
  );
}

/** The series, then the upcoming months named as projected: they are the paler bars (#380). */
function MonthsLegend({ upcoming, ...props }: { upcoming: string[] } & ComponentProps<typeof ChartLegendContent>) {
  const { t } = useTranslation();
  return (
    <div className="flex flex-wrap items-center justify-center gap-x-4 pt-3">
      <ChartLegendContent {...props} className="pt-0" />
      {upcoming.length > 0 ? (
        <span className="flex items-center gap-1.5 text-muted-foreground">
          <span aria-hidden="true" className="size-2.5 shrink-0 rounded-full" style={{ backgroundColor: 'var(--chart-1)', opacity: UPCOMING_OPACITY }} />
          {t('income.overview.projected', { months: upcoming.join(', '), count: upcoming.length })}
        </span>
      ) : null}
    </div>
  );
}

function MonthTooltip({ active, payload }: { active?: boolean; payload?: ReadonlyArray<{ payload?: Row }> }) {
  const { t } = useTranslation();
  const row = payload?.[0]?.payload;
  if (!active || !row) return null;
  const { month: m } = row;
  return (
    <div className="grid min-w-44 gap-1 rounded-lg border bg-popover/95 px-3 py-2 text-xs shadow-raised backdrop-blur-sm">
      <div className="font-medium">{monthLabel(m.year, m.month)}{m.upcoming ? ` · ${t('income.overview.upcoming')}` : ''}</div>
      {m.bySource.map((s) => (
        <div key={s.item} className="flex justify-between gap-4"><span>{s.name}</span><span className="tabular-nums">{formatMoney(s.amount)}</span></div>
      ))}
      {m.total ? <div className="flex justify-between gap-4 border-t pt-1 font-medium"><span>{t('income.overview.col_total')}</span><span className="tabular-nums">{formatMoney(m.total)}</span></div> : null}
      {m.previousYear ? <div className="flex justify-between gap-4 text-muted-foreground"><span>{t('income.overview.col_previous')}</span><span className="tabular-nums">{formatMoney(m.previousYear)}</span></div> : null}
    </div>
  );
}

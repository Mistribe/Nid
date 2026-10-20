import { useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Bar, BarChart, CartesianGrid, Cell, XAxis, YAxis } from 'recharts';
import { formatMoney } from '@/components/Money';
import { Button } from '@/components/ui/button';
import { Card, CardAction, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { ChartContainer, ChartLegend, ChartLegendContent, ChartTooltip, type ChartConfig } from '@/components/ui/chart';
import { toPlot } from '../../investments/chart/plot';
import type { IncomeOverview } from '../api';
import { monthLabel } from '../expected/months';
import { MonthsTable } from './MonthsTable';

type Months = IncomeOverview['months'];
type Month = Months[number];
type Row = { key: string; label: string; month: Month; recurring?: number; oneOff?: number; previous?: number };

const plot = (v?: { amount: string } | null) => (v ? toPlot(v.amount) : undefined);

/** Month by month: this period's cash as stacked bars (upcoming months dimmed) in front of last year's total. */
export function MonthsChart({ months, missingRates, period, year }: {
  months: Months; missingRates: string[]; period: 'year' | '12m' | '3y'; year: number;
}) {
  const { t } = useTranslation();
  const titleId = useId();
  const [asTable, setAsTable] = useState(false);
  const config = {
    previous: { label: t('income.overview.col_previous'), color: 'var(--muted-foreground)' },
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
  const dim = (r: Row) => (r.month.upcoming ? 0.4 : 1);
  // 36 months: thinner bars on a minimum width that scrolls sideways on a phone (spec §7).
  const wide = period === '3y';
  const [back, front] = wide ? [14, 8] : [28, 16];

  return (
    <Card role="region" aria-labelledby={titleId} className="gap-3 py-4">
      <CardHeader className="flex flex-wrap items-center gap-x-4 gap-y-2">
        <CardTitle id={titleId} className="mr-auto text-base">{title}</CardTitle>
        <CardAction className="static">
          <Button type="button" variant="outline" size="sm" onClick={() => setAsTable((v) => !v)}>
            {asTable ? t('income.overview.show_chart') : t('income.overview.show_table')}
          </Button>
        </CardAction>
      </CardHeader>
      <CardContent>
        {asTable
          ? <MonthsTable months={months} missingRates={missingRates} />
          : (
            <div className="overflow-x-auto">
              <ChartContainer config={config} className={wide ? 'h-64 w-full min-w-[48rem]' : 'h-64 w-full'}>
                <BarChart data={rows} title={t('income.overview.months_chart')} accessibilityLayer>
                  <CartesianGrid vertical={false} />
                  <XAxis xAxisId="now" dataKey="label" tickLine={false} tickMargin={8} />
                  <XAxis xAxisId="prev" dataKey="label" hide />
                  <YAxis width={64} tickLine={false} axisLine={false} tickFormatter={(n: number) => axis.format(n)} />
                  <ChartTooltip cursor={false} content={<MonthTooltip />} />
                  <ChartLegend content={<ChartLegendContent />} />
                  <Bar xAxisId="prev" dataKey="previous" fill="var(--color-previous)" fillOpacity={0.3} barSize={back} radius={[4, 4, 0, 0]} isAnimationActive={false} />
                  <Bar xAxisId="now" dataKey="recurring" stackId="now" fill="var(--color-recurring)" barSize={front} isAnimationActive={false}>
                    {rows.map((r) => <Cell key={r.key} fillOpacity={dim(r)} />)}
                  </Bar>
                  <Bar xAxisId="now" dataKey="oneOff" stackId="now" fill="var(--color-oneOff)" barSize={front} radius={[4, 4, 0, 0]} isAnimationActive={false}>
                    {rows.map((r) => <Cell key={r.key} fillOpacity={dim(r)} />)}
                  </Bar>
                </BarChart>
              </ChartContainer>
            </div>
          )}
      </CardContent>
    </Card>
  );
}

function MonthTooltip({ active, payload }: { active?: boolean; payload?: ReadonlyArray<{ payload?: Row }> }) {
  const { t } = useTranslation();
  const row = payload?.[0]?.payload;
  if (!active || !row) return null;
  const { month: m } = row;
  return (
    <div className="grid min-w-44 gap-1 rounded-lg border bg-background px-2.5 py-1.5 text-xs shadow-xl">
      <div className="font-medium">{monthLabel(m.year, m.month)}{m.upcoming ? ` · ${t('income.overview.upcoming')}` : ''}</div>
      {m.bySource.map((s) => (
        <div key={s.item} className="flex justify-between gap-4"><span>{s.name}</span><span className="tabular-nums">{formatMoney(s.amount)}</span></div>
      ))}
      {m.total ? <div className="flex justify-between gap-4 border-t pt-1 font-medium"><span>{t('income.overview.col_total')}</span><span className="tabular-nums">{formatMoney(m.total)}</span></div> : null}
      {m.previousYear ? <div className="flex justify-between gap-4 text-muted-foreground"><span>{t('income.overview.col_previous')}</span><span className="tabular-nums">{formatMoney(m.previousYear)}</span></div> : null}
    </div>
  );
}

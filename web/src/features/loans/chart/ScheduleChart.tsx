import { useTranslation } from 'react-i18next';
import { Bar, BarChart, CartesianGrid, Line, LineChart, XAxis, YAxis } from 'recharts';
import { ChartContainer, ChartLegend, ChartLegendContent, ChartTooltip, type ChartConfig } from '@/components/ui/chart';
import { NEUTRAL_COLOR } from '@/components/ProviderIcon';
import { toPlot } from '@/lib/plot';
import type { LoanScheduleYear } from '@/api/loans';
import { money } from '../format';
import '@/locales/loans';

type Row = {
  year: string; interest: number; principal: number; balance: number; interestText: string; principalText: string; balanceText: string;
};

/**
 * Spec §7: each year's interest and principal stacked, then the balance in a chart of its own (dataviz.md §2 and §6: one
 * vertical axis per chart). Every figure read — tooltip, summary, table — is the server's string; `toPlot` only places the marks.
 */
export function ScheduleChart({ years, currency, summaryId }: { years: LoanScheduleYear[]; currency: string; summaryId: string }) {
  const { t } = useTranslation();
  const config = {
    interest: { label: t('loan_detail.chart.interest'), color: NEUTRAL_COLOR },
    principal: { label: t('loan_detail.chart.principal'), color: 'var(--primary)' },
    balance: { label: t('loan_detail.chart.balance'), color: 'var(--chart-3)' },
  } satisfies ChartConfig;
  const rows: Row[] = years.map((y) => ({
    year: String(y.year),
    interest: toPlot(y.interest), principal: toPlot(y.principal), balance: toPlot(y.balance),
    interestText: money(currency, y.interest), principalText: money(currency, y.principal), balanceText: money(currency, y.balance),
  }));
  // Display of the plotting number only: an axis is a scale, not a figure the person reads exactly.
  const axis = new Intl.NumberFormat('fr-FR', { style: 'currency', currency, notation: 'compact' });
  const yAxis = <YAxis width={64} tickLine={false} axisLine={false} tickFormatter={(n: number) => axis.format(n)} />;
  return (
    <div className="grid gap-6 lg:grid-cols-2">
      <figure aria-label={t('loan_detail.chart.title')} aria-describedby={summaryId}>
        <ChartContainer config={config} className="h-64 w-full">
          <BarChart data={rows} maxBarSize={24} accessibilityLayer>
            <CartesianGrid vertical={false} />
            <XAxis dataKey="year" tickLine={false} tickMargin={8} minTickGap={24} />
            {yAxis}
            <ChartTooltip content={<YearTooltip />} />
            <ChartLegend content={<ChartLegendContent />} />
            <Bar dataKey="interest" stackId="booked" fill="var(--color-interest)" stroke="var(--card)" strokeWidth={2} animationDuration={800} />
            <Bar dataKey="principal" stackId="booked" fill="var(--color-principal)" stroke="var(--card)" strokeWidth={2} radius={[4, 4, 0, 0]} animationDuration={800} />
          </BarChart>
        </ChartContainer>
      </figure>
      <figure aria-label={t('loan_detail.chart.balance_title')}>
        <ChartContainer config={config} className="h-64 w-full">
          <LineChart data={rows} accessibilityLayer>
            <CartesianGrid vertical={false} />
            <XAxis dataKey="year" tickLine={false} tickMargin={8} minTickGap={24} />
            {yAxis}
            <ChartTooltip content={<YearTooltip />} />
            <ChartLegend content={<ChartLegendContent />} />
            <Line dataKey="balance" stroke="var(--color-balance)" strokeWidth={2} dot={false} animationDuration={800} />
          </LineChart>
        </ChartContainer>
      </figure>
    </div>
  );
}

function YearTooltip({ active, payload }: { active?: boolean; payload?: ReadonlyArray<{ payload?: Row }> }) {
  const { t } = useTranslation();
  const row = payload?.[0]?.payload;
  if (!active || !row) return null;
  return (
    <div className="grid gap-1 rounded-lg border bg-popover/95 px-3 py-2 text-xs shadow-raised backdrop-blur-sm">
      <div className="font-medium">{row.year}</div>
      <div className="tabular-nums">{t('loan_detail.chart.interest')} : {row.interestText}</div>
      <div className="tabular-nums">{t('loan_detail.chart.principal')} : {row.principalText}</div>
      <div className="tabular-nums">{t('loan_detail.chart.balance')} : {row.balanceText}</div>
    </div>
  );
}

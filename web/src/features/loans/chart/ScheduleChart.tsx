import { useTranslation } from 'react-i18next';
import { Bar, CartesianGrid, ComposedChart, Line, XAxis, YAxis } from 'recharts';
import { ChartContainer, ChartLegend, ChartLegendContent, ChartTooltip, type ChartConfig } from '@/components/ui/chart';
import { toPlot } from '../../investments/chart/plot';
import type { LoanScheduleYear } from '../api';
import { money } from '../format';

type Row = {
  year: string; interest: number; principal: number; balance: number; interestText: string; principalText: string; balanceText: string;
};

/**
 * Spec §7: each year's interest and principal stacked, and the balance as a line on its own axis (precision 17). Every
 * figure read — tooltip, summary, table — is the server's string; `toPlot` only places the marks.
 */
export function ScheduleChart({ years, currency, summaryId }: { years: LoanScheduleYear[]; currency: string; summaryId: string }) {
  const { t } = useTranslation();
  const config = {
    interest: { label: t('loan_detail.chart.interest'), color: 'var(--chart-1)' },
    principal: { label: t('loan_detail.chart.principal'), color: 'var(--chart-2)' },
    balance: { label: t('loan_detail.chart.balance'), color: 'var(--chart-3)' },
  } satisfies ChartConfig;
  const rows: Row[] = years.map((y) => ({
    year: String(y.year),
    interest: toPlot(y.interest), principal: toPlot(y.principal), balance: toPlot(y.balance),
    interestText: money(currency, y.interest), principalText: money(currency, y.principal), balanceText: money(currency, y.balance),
  }));
  // Display of the plotting number only: an axis is a scale, not a figure the person reads exactly.
  const axis = new Intl.NumberFormat('fr-FR', { style: 'currency', currency, notation: 'compact' });
  return (
    <figure aria-label={t('loan_detail.chart.title')} aria-describedby={summaryId}>
      <ChartContainer config={config} className="h-64 w-full">
        <ComposedChart data={rows} accessibilityLayer>
          <CartesianGrid vertical={false} />
          <XAxis dataKey="year" tickLine={false} tickMargin={8} />
          <YAxis yAxisId="booked" width={64} tickLine={false} axisLine={false} tickFormatter={(n: number) => axis.format(n)} />
          <YAxis yAxisId="balance" orientation="right" width={64} tickLine={false} axisLine={false} tickFormatter={(n: number) => axis.format(n)} />
          <ChartTooltip content={<YearTooltip />} />
          <ChartLegend content={<ChartLegendContent />} />
          <Bar yAxisId="booked" dataKey="interest" stackId="booked" fill="var(--color-interest)" isAnimationActive={false} />
          <Bar yAxisId="booked" dataKey="principal" stackId="booked" fill="var(--color-principal)" radius={[4, 4, 0, 0]} isAnimationActive={false} />
          <Line yAxisId="balance" dataKey="balance" stroke="var(--color-balance)" strokeWidth={2} dot={false} isAnimationActive={false} />
        </ComposedChart>
      </ChartContainer>
    </figure>
  );
}

function YearTooltip({ active, payload }: { active?: boolean; payload?: ReadonlyArray<{ payload?: Row }> }) {
  const { t } = useTranslation();
  const row = payload?.[0]?.payload;
  if (!active || !row) return null;
  return (
    <div className="grid gap-1 rounded-lg border bg-background px-2.5 py-1.5 text-xs shadow-xl">
      <div className="font-medium">{row.year}</div>
      <div className="tabular-nums">{t('loan_detail.chart.interest')} : {row.interestText}</div>
      <div className="tabular-nums">{t('loan_detail.chart.principal')} : {row.principalText}</div>
      <div className="tabular-nums">{t('loan_detail.chart.balance')} : {row.balanceText}</div>
    </div>
  );
}

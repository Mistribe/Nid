import { useTranslation } from 'react-i18next';
import { CartesianGrid, ComposedChart, Line, ReferenceDot, XAxis, YAxis } from 'recharts';
import { formatCurrency } from '@/components/Money';
import { ChartContainer, ChartTooltip, type ChartConfig } from '@/components/ui/chart';
import { monthLabel } from '@/lib/format';
import { toPlot } from '@/lib/plot';
import type { RetirementView } from './api';

const month = (yyyyMm: string) => {
  const [year, m] = yyyyMm.split('-').map(Number);
  return monthLabel(year ?? 0, m ?? 1, 'long');
};

type Row = { on: string; covered: number; expenses: number; coveredText: string; expensesText: string };

/** Retirement spec §5: the income the capital covers against the expenses, month by month in today's money; the independence month marked. */
export default function IndependenceChart({ data }: { data: RetirementView }) {
  const { t } = useTranslation();
  const config = {
    covered: { label: t('retirement.covered'), color: 'var(--chart-1)' },
    expenses: { label: t('retirement.expenses'), color: 'var(--chart-2)' },
  } satisfies ChartConfig;
  const rows: Row[] = data.independence.map((p) => ({
    on: p.on, covered: toPlot(p.covered), expenses: toPlot(p.expenses),
    coveredText: formatCurrency(p.covered, data.currency), expensesText: formatCurrency(p.expenses, data.currency),
  }));
  const fi = data.financialIndependence;
  const dot = fi ? rows.find((r) => r.on.startsWith(fi)) : undefined;
  // One tick a year, on the January points.
  const ticks = rows.filter((r) => r.on.slice(5, 7) === '01').map((r) => r.on);
  const axis = new Intl.NumberFormat('fr-FR', { style: 'currency', currency: data.currency, notation: 'compact' });
  return (
    <div className="space-y-2">
      <ul aria-label={t('retirement.legend')} className="flex flex-wrap gap-x-4 gap-y-1 text-sm">
        <li className="flex items-center gap-1.5"><span aria-hidden="true" className="h-0.5 w-4 rounded bg-chart-1" />{t('retirement.covered')}</li>
        <li className="flex items-center gap-1.5"><span aria-hidden="true" className="w-4 border-t-2 border-dashed border-chart-2" />{t('retirement.expenses')}</li>
      </ul>
      <div role="img" aria-label={t('retirement.chart_summary', { date: fi ? month(fi) : t('retirement.independence_unreached') })}>
        <ChartContainer config={config} className="h-64 w-full">
          <ComposedChart data={rows} accessibilityLayer={false}>
            <CartesianGrid vertical={false} />
            <XAxis dataKey="on" ticks={ticks} tickFormatter={(on: string) => on.slice(0, 4)} tickLine={false} axisLine={false} tickMargin={8} />
            <YAxis width={64} tickLine={false} axisLine={false} tickFormatter={(n: number) => axis.format(n)} />
            <ChartTooltip content={<IndependenceTooltip />} />
            <Line dataKey="covered" type="monotone" stroke="var(--color-covered)" strokeWidth={2} dot={false} activeDot={{ r: 4 }} isAnimationActive={false} />
            <Line dataKey="expenses" type="monotone" stroke="var(--color-expenses)" strokeWidth={2} strokeDasharray="4 4" dot={false} activeDot={{ r: 4 }} isAnimationActive={false} />
            {dot ? <ReferenceDot x={dot.on} y={dot.covered} r={4} fill="var(--color-covered)" stroke="var(--background)" /> : null}
          </ComposedChart>
        </ChartContainer>
      </div>
    </div>
  );
}

function IndependenceTooltip({ active, payload }: { active?: boolean; payload?: ReadonlyArray<{ payload?: Row }> }) {
  const { t } = useTranslation();
  const row = payload?.[0]?.payload;
  if (!active || !row) return null;
  return (
    <div className="grid gap-1 rounded-xl border bg-background px-3 py-2 text-xs shadow-md">
      <div className="font-medium">{month(row.on.slice(0, 7))}</div>
      <div className="tabular-nums">{t('retirement.covered')} : {row.coveredText}</div>
      <div className="tabular-nums">{t('retirement.expenses')} : {row.expensesText}</div>
    </div>
  );
}

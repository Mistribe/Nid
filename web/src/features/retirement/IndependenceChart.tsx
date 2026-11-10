import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { CartesianGrid, ComposedChart, Line, ReferenceDot, XAxis, YAxis } from 'recharts';
import { formatCurrency } from '@/components/Money';
import { ChartContainer, ChartTooltip, type ChartConfig } from '@/components/ui/chart';
import { TableToggle } from '@/components/page/TableToggle';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { yyyyMmLabel as month } from '@/lib/format';
import { toPlot } from '@/lib/plot';
import type { RetirementView } from './api';

type Row = { on: string; covered: number; expenses: number; coveredText: string; expensesText: string };

/** Retirement spec §5: the income the capital covers against the expenses, month by month in today's money; the independence month marked. */
export default function IndependenceChart({ data, livingCostMissing }: { data: RetirementView; livingCostMissing: boolean }) {
  const { t } = useTranslation();
  const [asTable, setAsTable] = useState(false);
  const config = {
    covered: { label: t('retirement.covered'), color: 'var(--chart-1)' },
    expenses: { label: t('retirement.expenses'), color: 'var(--chart-2)' },
  } satisfies ChartConfig;
  const rows: Row[] = data.independence.map((p) => ({
    on: p.on, covered: toPlot(p.covered), expenses: toPlot(p.expenses),
    coveredText: formatCurrency(p.covered, data.currency), expensesText: formatCurrency(p.expenses, data.currency),
  }));
  // financialIndependence is a yyyy-MM and always a month of the series; with no living cost it is null, not « unreached ».
  const fi = data.financialIndependence;
  const date = fi ? month(fi) : livingCostMissing ? t('retirement.independence_not_computed') : t('retirement.independence_unreached');
  const dot = fi ? rows.find((r) => r.on.startsWith(fi)) : undefined;
  // One tick a year, on the January points.
  const ticks = rows.filter((r) => r.on.slice(5, 7) === '01').map((r) => r.on);
  const axis = new Intl.NumberFormat('fr-FR', { style: 'currency', currency: data.currency, notation: 'compact' });
  return (
    <div className="space-y-2">
      <div className="flex flex-wrap items-center justify-between gap-2">
      <ul aria-label={t('retirement.legend')} className="flex flex-wrap gap-x-4 gap-y-1 text-sm">
        <li className="flex items-center gap-1.5"><span aria-hidden="true" className="h-0.5 w-4 rounded bg-chart-1" />{t('retirement.covered')}</li>
        <li className="flex items-center gap-1.5"><span aria-hidden="true" className="w-4 border-t-2 border-dashed border-chart-2" />{t('retirement.expenses')}</li>
      </ul>
      <TableToggle asTable={asTable} onToggle={() => setAsTable((v) => !v)} />
      </div>
      {asTable ? <IndependenceTable data={data} /> : (
      <div role="img" aria-label={t('retirement.chart_summary', { date })}>
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
      )}
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

function IndependenceTable({ data }: { data: RetirementView }) {
  const { t } = useTranslation();
  const last = data.independence.length - 1;
  const money = (amount: string) => formatCurrency(amount, data.currency);
  const rows = data.independence.filter((p, i) => p.on.slice(5, 7) === '12' || i === last);
  return (
    <Table aria-label={t('retirement.table')}>
      <TableHeader>
        <TableRow>
          <TableHead scope="col">{t('retirement.date')}</TableHead>
          <TableHead scope="col" className="text-right">{t('retirement.covered')}</TableHead>
          <TableHead scope="col" className="text-right">{t('retirement.expenses')}</TableHead>
          <TableHead scope="col" className="text-right">{t('retirement.capital')}</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {rows.map((p) => (
          <TableRow key={p.on}>
            <TableHead scope="row" className="font-normal">{month(p.on.slice(0, 7))}</TableHead>
            <TableCell className="text-right tabular-nums">{money(p.covered)}</TableCell>
            <TableCell className="text-right tabular-nums">{money(p.expenses)}</TableCell>
            <TableCell className="text-right tabular-nums">{money(p.capital)}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}

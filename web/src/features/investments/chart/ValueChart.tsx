import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Area, CartesianGrid, ComposedChart, Line, XAxis, YAxis } from 'recharts';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { formatLocalDate, LocalDate } from '@/components/LocalDate';
import { formatMoney, Money } from '@/components/Money';
import { ProblemMessage } from '@/components/ProblemMessage';
import { ChartContainer, ChartLegend, ChartLegendContent, ChartTooltip, type ChartConfig } from '@/components/ui/chart';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import type { components } from '@/api/schema';
import { monthLabel } from '@/lib/format';
import { SERIES } from '../api';
import { toPlot } from '@/lib/plot';
import { TableToggle } from '@/components/page/TableToggle';
import '@/locales/investments';

// `at` places the point in time: an axis of dates evenly spaced would squeeze the sparse years and stretch the dense ones.
type Row = { asOf: string; at: number; value: number; net: number; valueText: string; netText: string; paidIn: boolean };
type Point = components['schemas']['SeriesPoint'];

/** The envelope's value against its net contributions over time; the same points are one button away as a table. */
export function ValueChart({ envelopeId, currency }: { envelopeId: string; currency: string }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const answer = $api.useQuery('get', SERIES, { params: { path: { id: envelopeId } } });

  if (answer.error) return <ProblemMessage problem={present(toProblem(answer.error))} />;
  if (!answer.data) return <div role="status" aria-busy="true" aria-label={t('envelope.chart_loading')} className="h-64 shimmer rounded-md border bg-muted" />;
  return <SeriesChart id={envelopeId} points={answer.data.points} currency={currency} netLabel={t('envelope.net_contributions')}
    title={t('envelope.chart_title')} tableLabel={t('envelope.chart_table')} />;
}

/** A value curve against a stepped net-money line, shared by the envelope and a holding (whose net is money in − out). */
export function SeriesChart({ id, points, currency, netLabel, title, tableLabel }: {
  id: string; points: Point[]; currency: string; netLabel: string; title: string; tableLabel: string;
}) {
  const { t } = useTranslation();
  const [asTable, setAsTable] = useState(false);
  if (points.length < 2) return <p className="text-muted-foreground">{t('envelope.chart_needs_two')}</p>;

  const config = {
    value: { label: t('envelope.value'), color: 'var(--chart-1)' },
    net: { label: netLabel, color: 'var(--chart-2)' },
  } satisfies ChartConfig;
  const rows: Row[] = points.map((p) => ({
    asOf: p.asOf,
    at: Date.parse(p.asOf),
    value: toPlot(p.value.amount),
    net: toPlot(p.netContributions.amount),
    valueText: formatMoney(p.value),
    netText: formatMoney(p.netContributions),
    paidIn: p.paidIn,
  }));
  // Display of the plotting number only: the axis is a scale, not a figure the person reads exactly.
  const axis = new Intl.NumberFormat('fr-FR', { style: 'currency', currency, notation: 'compact' });
  const time = timeTicks(Math.min(...rows.map((r) => r.at)), Math.max(...rows.map((r) => r.at)));

  return (
    <div className="space-y-2">
      <div className="flex justify-end">
        <TableToggle asTable={asTable} onToggle={() => setAsTable((v) => !v)} />
      </div>
      {asTable ? (
        <Table aria-label={tableLabel}>
          <TableHeader>
            <TableRow>
              <TableHead>{t('envelope.date')}</TableHead>
              <TableHead>{t('envelope.value')}</TableHead>
              <TableHead>{netLabel}</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {points.map((p) => (
              <TableRow key={p.asOf}>
                <TableCell><LocalDate value={p.asOf} /></TableCell>
                <TableCell><Money value={p.value} tabular />{p.paidIn ? <PaidIn /> : null}</TableCell>
                <TableCell><Money value={p.netContributions} tabular /></TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      ) : (
        <ChartContainer config={config} className="h-64 w-full">
          <ComposedChart data={rows} title={title}>
            <defs>
              <linearGradient id={`fill-${id}`} x1="0" y1="0" x2="0" y2="1">
                <stop offset="5%" stopColor="var(--color-value)" stopOpacity={0.2} />
                <stop offset="95%" stopColor="var(--color-value)" stopOpacity={0.02} />
              </linearGradient>
            </defs>
            <CartesianGrid vertical={false} />
            <XAxis dataKey="at" type="number" scale="time" domain={['dataMin', 'dataMax']} tickLine={false} tickMargin={8}
              ticks={time.ticks} tickFormatter={time.format} />
            <YAxis width={64} tickLine={false} axisLine={false} tickFormatter={(n: number) => axis.format(n)} />
            <ChartTooltip content={<ValueTooltip netLabel={netLabel} />} />
            <ChartLegend content={<ChartLegendContent />} />
            <Area dataKey="value" type="monotone" stroke="var(--color-value)" strokeWidth={2} fill={`url(#fill-${id})`} dot={false} animationDuration={800} />
            <Line dataKey="net" stroke="var(--color-net)" strokeWidth={2} strokeDasharray="4 4" type="stepAfter" dot={false} animationDuration={800} />
          </ComposedChart>
        </ChartContainer>
      )}
    </div>
  );
}

/**
 * Round dates for a time axis (#381), evenly spaced: each January (every few years past eight) over more than two years,
 * else the first of every one, two or three months, eight ticks at most. Under a month there is no round date: the data's own.
 */
export function timeTicks(from: number, to: number): { ticks?: number[]; format: (at: number) => string } {
  const start = new Date(from);
  const end = new Date(to);
  const months = (end.getUTCFullYear() - start.getUTCFullYear()) * 12 + end.getUTCMonth() - start.getUTCMonth();
  const years = months > 24;
  const step = years ? Math.ceil(months / 12 / 8) * 12 : [1, 2, 3].find((s) => months / s <= 8) ?? 3;
  const ticks: number[] = [];
  for (let index = start.getUTCFullYear() * 12 + start.getUTCMonth(); index <= end.getUTCFullYear() * 12 + end.getUTCMonth(); index++) {
    const at = Date.UTC(Math.floor(index / 12), index % 12, 1);
    if (index % step === 0 && at >= from && at <= to) ticks.push(at);
  }
  if (ticks.length === 0) return { format: (at) => formatLocalDate(new Date(at).toISOString().slice(0, 10), 'short') };
  return {
    ticks,
    format: (at) => {
      const date = new Date(at);
      return years ? String(date.getUTCFullYear()) : monthLabel(date.getUTCFullYear(), date.getUTCMonth() + 1, 'short');
    },
  };
}

function ValueTooltip({ active, payload, netLabel }: { active?: boolean; payload?: ReadonlyArray<{ payload?: Row }>; netLabel: string }) {
  const { t } = useTranslation();
  const row = payload?.[0]?.payload;
  if (!active || !row) return null;
  return (
    <div className="grid gap-1 rounded-lg border bg-popover/95 px-3 py-2 text-xs shadow-raised backdrop-blur-sm">
      <div className="font-medium">{formatLocalDate(row.asOf)}</div>
      <div className="flex justify-between gap-4"><span>{t('envelope.value')}</span><span className="tabular-nums">{row.valueText}</span></div>
      {row.paidIn ? <PaidIn /> : null}
      <div className="flex justify-between gap-4"><span>{netLabel}</span><span className="tabular-nums">{row.netText}</span></div>
    </div>
  );
}

/** Issue #325: before its first valuation, an envelope's value is what was paid in: said in words. */
function PaidIn() {
  const { t } = useTranslation();
  return <span className="block text-xs text-muted-foreground">{t('envelope.paid_in')}</span>;
}

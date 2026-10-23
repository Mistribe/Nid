import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Area, CartesianGrid, ComposedChart, Line, XAxis, YAxis } from 'recharts';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { formatLocalDate, LocalDate } from '@/components/LocalDate';
import { formatMoney, Money } from '@/components/Money';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { ChartContainer, ChartLegend, ChartLegendContent, ChartTooltip, type ChartConfig } from '@/components/ui/chart';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import type { components } from '@/api/schema';
import { SERIES } from '../api';
import { toPlot } from './plot';
import '@/locales/investments';

// `at` places the point in time: an axis of dates evenly spaced would squeeze the sparse years and stretch the dense ones.
type Row = { asOf: string; at: number; value: number; net: number; valueText: string; netText: string };
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
  }));
  // Display of the plotting number only: the axis is a scale, not a figure the person reads exactly.
  const axis = new Intl.NumberFormat('fr-FR', { style: 'currency', currency, notation: 'compact' });

  return (
    <div className="space-y-2">
      <div className="flex justify-end">
        <Button type="button" variant="outline" size="sm" onClick={() => setAsTable((v) => !v)}>
          {asTable ? t('common.show_chart') : t('common.show_table')}
        </Button>
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
                <TableCell><Money value={p.value} tabular /></TableCell>
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
                <stop offset="5%" stopColor="var(--color-value)" stopOpacity={0.35} />
                <stop offset="95%" stopColor="var(--color-value)" stopOpacity={0.02} />
              </linearGradient>
            </defs>
            <CartesianGrid vertical={false} />
            <XAxis dataKey="at" type="number" scale="time" domain={['dataMin', 'dataMax']} tickLine={false} tickMargin={8}
              tickFormatter={(at: number) => formatLocalDate(new Date(at).toISOString().slice(0, 10), 'short')} />
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

function ValueTooltip({ active, payload, netLabel }: { active?: boolean; payload?: ReadonlyArray<{ payload?: Row }>; netLabel: string }) {
  const { t } = useTranslation();
  const row = payload?.[0]?.payload;
  if (!active || !row) return null;
  return (
    <div className="grid gap-1 rounded-lg border bg-background px-2.5 py-1.5 text-xs shadow-xl">
      <div className="font-medium">{formatLocalDate(row.asOf)}</div>
      <div className="flex justify-between gap-4"><span>{t('envelope.value')}</span><span className="tabular-nums">{row.valueText}</span></div>
      <div className="flex justify-between gap-4"><span>{netLabel}</span><span className="tabular-nums">{row.netText}</span></div>
    </div>
  );
}

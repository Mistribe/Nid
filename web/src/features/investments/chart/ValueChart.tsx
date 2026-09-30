import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { CartesianGrid, Line, LineChart, XAxis, YAxis } from 'recharts';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { formatLocalDate, LocalDate } from '@/components/LocalDate';
import { formatMoney, Money } from '@/components/Money';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { ChartContainer, ChartLegend, ChartLegendContent, ChartTooltip, type ChartConfig } from '@/components/ui/chart';
import { SERIES } from '../api';
import { toPlot } from './plot';

type Row = { asOf: string; value: number; net: number; valueText: string; netText: string };

/** The envelope's value against its net contributions over time; the same points are one button away as a table. */
export function ValueChart({ envelopeId, currency }: { envelopeId: string; currency: string }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const [asTable, setAsTable] = useState(false);
  const answer = $api.useQuery('get', SERIES, { params: { path: { id: envelopeId } } });

  if (answer.error) return <ProblemMessage problem={present(toProblem(answer.error))} />;
  if (!answer.data) return <div aria-busy="true" className="h-64 animate-pulse rounded-md border bg-muted" />;

  const points = answer.data.points;
  if (points.length < 2) return <p className="text-muted-foreground">{t('envelope.chart_needs_two')}</p>;

  const config = {
    value: { label: t('envelope.value'), color: 'var(--chart-1)' },
    net: { label: t('envelope.net_contributions'), color: 'var(--chart-2)' },
  } satisfies ChartConfig;
  const rows: Row[] = points.map((p) => ({
    asOf: p.asOf,
    value: toPlot(p.value.amount),
    net: toPlot(p.netContributions.amount),
    valueText: formatMoney(p.value),
    netText: formatMoney(p.netContributions),
  }));
  // Display of the plotting number only: the axis is a scale, not a figure the person reads exactly.
  const axis = new Intl.NumberFormat('fr-FR', { style: 'currency', currency, notation: 'compact' });

  return (
    <div className="space-y-2">
      <Button type="button" variant="outline" size="sm" aria-pressed={asTable} onClick={() => setAsTable((v) => !v)}>
        {asTable ? t('envelope.show_chart') : t('envelope.show_table')}
      </Button>
      {asTable ? (
        <table className="w-full text-sm" aria-label={t('envelope.chart_table')}>
          <thead>
            <tr>
              <th scope="col" className="text-left">{t('envelope.date')}</th>
              <th scope="col" className="text-left">{t('envelope.value')}</th>
              <th scope="col" className="text-left">{t('envelope.net_contributions')}</th>
            </tr>
          </thead>
          <tbody>
            {points.map((p) => (
              <tr key={p.asOf}>
                <td><LocalDate value={p.asOf} /></td>
                <td><Money value={p.value} tabular /></td>
                <td><Money value={p.netContributions} tabular /></td>
              </tr>
            ))}
          </tbody>
        </table>
      ) : (
        <ChartContainer config={config} className="h-64 w-full">
          <LineChart data={rows} title={t('envelope.chart_title')}>
            <CartesianGrid vertical={false} />
            <XAxis dataKey="asOf" tickLine={false} tickMargin={8} tickFormatter={(d: string) => formatLocalDate(d, 'short')} />
            <YAxis width={64} tickLine={false} axisLine={false} tickFormatter={(n: number) => axis.format(n)} />
            <ChartTooltip content={<ValueTooltip />} />
            <ChartLegend content={<ChartLegendContent />} />
            <Line dataKey="value" stroke="var(--color-value)" strokeWidth={2} dot isAnimationActive={false} />
            <Line dataKey="net" stroke="var(--color-net)" strokeWidth={2} type="stepAfter" dot={false} isAnimationActive={false} />
          </LineChart>
        </ChartContainer>
      )}
    </div>
  );
}

function ValueTooltip({ active, payload }: { active?: boolean; payload?: ReadonlyArray<{ payload?: Row }> }) {
  const { t } = useTranslation();
  const row = payload?.[0]?.payload;
  if (!active || !row) return null;
  return (
    <div className="grid gap-1 rounded-lg border bg-background px-2.5 py-1.5 text-xs shadow-xl">
      <div className="font-medium">{formatLocalDate(row.asOf)}</div>
      <div className="flex justify-between gap-4"><span>{t('envelope.value')}</span><span className="tabular-nums">{row.valueText}</span></div>
      <div className="flex justify-between gap-4"><span>{t('envelope.net_contributions')}</span><span className="tabular-nums">{row.netText}</span></div>
    </div>
  );
}
